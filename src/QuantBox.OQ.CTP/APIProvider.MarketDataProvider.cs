﻿using System;
using System.ComponentModel;
using SmartQuant.Data;
using SmartQuant.FIX;
using SmartQuant.Instruments;
using SmartQuant.Providers;
using QuantBox.OQ.CTP;

#if CTP
using QuantBox.CSharp2CTP;
using QuantBox.Helper.CTP;

namespace QuantBox.OQ.CTP
#elif CTPZQ
using QuantBox.CSharp2CTPZQ;
using QuantBox.Helper.CTPZQ;

namespace QuantBox.OQ.CTPZQ
#endif
{
    public partial class APIProvider:IMarketDataProvider
    {
#if OQ
        public IMarketDataFilter MarketDataFilter { get; set; }
#endif
        private IBarFactory factory;

        public event MarketDataRequestRejectEventHandler MarketDataRequestReject;
        public event MarketDataSnapshotEventHandler MarketDataSnapshot;
        public event BarEventHandler NewBar;
        public event BarEventHandler NewBarOpen;
        public event BarSliceEventHandler NewBarSlice;
        public event CorporateActionEventHandler NewCorporateAction;
        public event FundamentalEventHandler NewFundamental;
        public event BarEventHandler NewMarketBar;
        public event MarketDataEventHandler NewMarketData;
        public event MarketDepthEventHandler NewMarketDepth;
        public event QuoteEventHandler NewQuote;
        public event TradeEventHandler NewTrade;        

        #region IMarketDataProvider
        [Category(CATEGORY_BARFACTORY)]
        public IBarFactory BarFactory
        {
            get
            {
                return factory;
            }
            set
            {
                if (factory != null)
                {
                    factory.NewBar -= OnNewBar;
                    factory.NewBarOpen -= OnNewBarOpen;
                    factory.NewBarSlice -= OnNewBarSlice;
                }
                factory = value;
                if (factory != null)
                {
                    factory.NewBar += OnNewBar;
                    factory.NewBarOpen += OnNewBarOpen;
                    factory.NewBarSlice += OnNewBarSlice;
                }
            }
        }

        private void OnNewBarSlice(object sender, BarSliceEventArgs args)
        {
            if (NewBarSlice != null)
            {
                NewBarSlice(this, new BarSliceEventArgs(args.BarSize, this));
            }
        }

        public void SendMarketDataRequest(FIXMarketDataRequest request)
        {
            if (!_bMdConnected)
            {
                EmitError(-1, -1, "行情服务器没有连接");
                mdlog.Error("行情服务器没有连接");
                return;
            }

            bool bSubscribe = false;
            bool bTrade = false;
            bool bQuote = false;
            bool bMarketDepth = false;
            if (request.NoMDEntryTypes > 0)
            {
                switch (request.GetMDEntryTypesGroup(0).MDEntryType)
                {
                    case FIXMDEntryType.Bid:
                    case FIXMDEntryType.Offer:
                        if (request.MarketDepth != 1)
                        {
                            bMarketDepth = true;
                            break;
                        }
                        bQuote = true;
                        break;
                    case FIXMDEntryType.Trade:
                        bTrade = true;
                        break;
                }
            }
            bSubscribe = (request.SubscriptionRequestType == DataManager.MARKET_DATA_SUBSCRIBE);

            if (bSubscribe)
            {
                for (int i = 0; i < request.NoRelatedSym; ++i)
                {
                    FIXRelatedSymGroup group = request.GetRelatedSymGroup(i);
                    Instrument inst = InstrumentManager.Instruments[group.Symbol];

                    //将用户合约转成交易所合约
                    string altSymbol = inst.GetSymbol(this.Name);
                    string altExchange = inst.GetSecurityExchange(this.Name);
                    string apiSymbol = GetApiSymbol(altSymbol);
                    string apiExchange = altExchange;
#if CTPZQ
                    altSymbol = GetYahooSymbol(apiSymbol, apiExchange);
#endif
                    CThostFtdcInstrumentField _Instrument;
                    if (_dictInstruments.TryGetValue(altSymbol, out _Instrument))
                    {
                        apiSymbol = _Instrument.InstrumentID;
                        apiExchange = _Instrument.ExchangeID;
                    }

#if CTPZQ
                    altSymbol = GetYahooSymbol(apiSymbol, apiExchange);
#endif
                    DataRecord record;
                    if (!_dictAltSymbol2Instrument.TryGetValue(altSymbol, out record))
                    {
                        record = new DataRecord();
                        record.Instrument = inst;
                        record.Symbol = apiSymbol;
                        record.Exchange = apiExchange;
                        _dictAltSymbol2Instrument[altSymbol] = record;

                        mdlog.Info("订阅合约/订阅询价 {0} {1} {2}", altSymbol, record.Symbol, record.Exchange);

                        if (_bTdConnected)
                        {
                            TraderApi.TD_ReqQryInvestorPosition(m_pTdApi, null);
                            timerPonstion.Enabled = false;
                            timerPonstion.Enabled = true;
                        }
                    }

                    //记录行情,同时对用户合约与交易所合约进行映射
                    CThostFtdcDepthMarketDataField DepthMarket;
                    if (!_dictDepthMarketData.TryGetValue(altSymbol, out DepthMarket))
                    {
                        _dictDepthMarketData[altSymbol] = DepthMarket;
                    }

                    // 多次订阅也无所谓
                    MdApi.MD_Subscribe(m_pMdApi, record.Symbol, record.Exchange);
                    MdApi.MD_SubscribeQuote(m_pMdApi, record.Symbol, record.Exchange);

                    if (bTrade)
                        record.TradeRequested = true;
                    if (bQuote)
                        record.QuoteRequested = true;
                    if (bMarketDepth)
                        record.MarketDepthRequested = true;

                    if (bMarketDepth)
                    {
                        inst.OrderBook.Clear();
                    }
                }
            }
            else
            {
                for (int i = 0; i < request.NoRelatedSym; ++i)
                {
                    FIXRelatedSymGroup group = request.GetRelatedSymGroup(i);
                    Instrument inst = InstrumentManager.Instruments[group.Symbol];

                    //将用户合约转成交易所合约
                    string altSymbol = inst.GetSymbol(this.Name);
                    string altExchange = inst.GetSecurityExchange(this.Name);

                    DataRecord record;
                    if (!_dictAltSymbol2Instrument.TryGetValue(altSymbol, out record))
                    {
                        break;
                    }

                    if (bTrade)
                        record.TradeRequested = false;
                    if (bQuote)
                        record.QuoteRequested = false;
                    if (bMarketDepth)
                        record.MarketDepthRequested = false;

                    if (!record.TradeRequested && !record.QuoteRequested && !record.MarketDepthRequested)
                    {
                        _dictDepthMarketData.Remove(altSymbol);
                        _dictAltSymbol2Instrument.Remove(altSymbol);
                        mdlog.Info("取消合约/取消询价 {0} {1} {2}", altSymbol, record.Symbol, record.Exchange);
                        MdApi.MD_Unsubscribe(m_pMdApi, record.Symbol, record.Exchange);
                        MdApi.MD_UnsubscribeQuote(m_pMdApi, record.Symbol, record.Exchange);
                    }
                    else
                    {
                        // 只要有一种类型说要订阅，就给订上
                        MdApi.MD_Subscribe(m_pMdApi, record.Symbol, record.Exchange);
                        MdApi.MD_SubscribeQuote(m_pMdApi, record.Symbol, record.Exchange);
                    }
                }
            }
        }

        private bool EmitNewMarketDepth(Instrument instrument, DateTime datatime, int position, MDSide ask, double price, int size)
        {
            bool bRet = false;
            MDOperation insert = MDOperation.Update;
            if (MDSide.Ask == ask)
            {
                if (position >= instrument.OrderBook.Ask.Count)
                {
                    insert = MDOperation.Insert;
                }
            }
            else
            {
                if (position >= instrument.OrderBook.Bid.Count)
                {
                    insert = MDOperation.Insert;
                }
            }

            if (price != 0 && size != 0)
            {
                EmitNewMarketDepth(instrument, new MarketDepth(datatime, "", position, insert, ask, price, size));
                bRet = true;
            }
            return bRet;
        }

        private void EmitNewMarketDepth(IFIXInstrument instrument, MarketDepth marketDepth)
        {
            if (NewMarketDepth != null)
            {
                NewMarketDepth(this, new MarketDepthEventArgs(marketDepth, instrument, this));
            }
        }
        #endregion

        private void OnNewBar(object sender, BarEventArgs args)
        {
            if (NewBar != null)
            {
                CThostFtdcDepthMarketDataField DepthMarket;
                Instrument inst = InstrumentManager.Instruments[args.Instrument.Symbol];
                string altSymbol = inst.GetSymbol(Name);

                Bar bar = args.Bar;
                if (_dictDepthMarketData.TryGetValue(altSymbol, out DepthMarket))
                {
                    bar = new Bar(args.Bar);
                    bar.OpenInt = (long)DepthMarket.OpenInterest;
                }
#if OQ
                if (null != MarketDataFilter)
                {
                    Bar b = MarketDataFilter.FilterBar(bar, args.Instrument.Symbol);
                    if (null != b)
                    {
                        NewBar(this, new BarEventArgs(b, args.Instrument, this));
                    }
                }
                else
#endif
                {
                    NewBar(this, new BarEventArgs(bar, args.Instrument, this));
                }
            }
        }

        private void OnNewBarOpen(object sender, BarEventArgs args)
        {
            if (NewBarOpen != null)
            {
                CThostFtdcDepthMarketDataField DepthMarket;
                Instrument inst = InstrumentManager.Instruments[args.Instrument.Symbol];
                string altSymbol = inst.GetSymbol(Name);

                Bar bar = args.Bar;
                if (_dictDepthMarketData.TryGetValue(altSymbol, out DepthMarket))
                {
                    bar = new Bar(args.Bar);
                    bar.OpenInt = (long)DepthMarket.OpenInterest;
                }

#if OQ
                if (null != MarketDataFilter)
                {
                    Bar b = MarketDataFilter.FilterBarOpen(bar, args.Instrument.Symbol);
                    if (null != b)
                    {
                        NewBarOpen(this, new BarEventArgs(b, args.Instrument, this));
                    }
                }
                else
#endif
                {
                    NewBarOpen(this, new BarEventArgs(bar, args.Instrument, this));
                }
            }
        }

        private void EmitNewQuoteEvent(IFIXInstrument instrument, Quote quote)
        {
#if OQ
            if (this.MarketDataFilter != null)
            {
                quote = this.MarketDataFilter.FilterQuote(quote, instrument.Symbol);
            }
#endif
            if (quote != null)
            {
                if (NewQuote != null)
                {
                    NewQuote(this, new QuoteEventArgs(quote, instrument, this));
                }
                if (factory != null)
                {
                    factory.OnNewQuote(instrument, quote);
                }
            }
        }

        private void EmitNewTradeEvent(IFIXInstrument instrument, Trade trade)
        {
#if OQ
            if (this.MarketDataFilter != null)
            {
                trade = this.MarketDataFilter.FilterTrade(trade, instrument.Symbol);
            }
#endif
            if (trade != null)
            {
                if (NewTrade != null)
                {
                    NewTrade(this, new TradeEventArgs(trade, instrument, this));
                }
                if (factory != null)
                {
                    factory.OnNewTrade(instrument, trade);
                }
            }
        }
    }
}
