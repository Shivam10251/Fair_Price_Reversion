//+------------------------------------------------------------------+
//| FPMR · Strategy engine · per-bar bodies                          |
//|                                                                  |
//| Included from FpmrStrategy.mqh, never on its own. The call order  |
//| in OnClosedBar is load-bearing and mirrors the Pine script and    |
//| the NT8 OnBarUpdate exactly - changing it changes the signals.    |
//+------------------------------------------------------------------+
#ifndef FPMR_STRATEGY_IMPL_MQH
#define FPMR_STRATEGY_IMPL_MQH

//+------------------------------------------------------------------+
//| Loads the closed-bar snapshot and the pivot windows.              |
//| Everything is taken from shift 1 onward: shift 0 is still forming.|
//+------------------------------------------------------------------+
bool CFpmrStrategy::LoadBarWindow(const int needed)
  {
   MqlRates r[];
   ArraySetAsSeries(r,true);

   if(CopyRates(_Symbol,_Period,1,1,r)<1)
      return(false);

   m_barOpen     = r[0].open;
   m_barHigh     = r[0].high;
   m_barLow      = r[0].low;
   m_barClose    = r[0].close;
   m_barOpenTime = r[0].time;

   bool useReal=(m_cfg.volumeMode==FP_VOL_REAL)
                || (m_cfg.volumeMode==FP_VOL_AUTO && r[0].real_volume>0);
   m_barVolume=(useReal ? (double)r[0].real_volume : (double)r[0].tick_volume);

   ArraySetAsSeries(m_highs,true);
   ArraySetAsSeries(m_lows,true);

   if(CopyHigh(_Symbol,_Period,1,needed,m_highs)<needed)
      return(false);
   if(CopyLow(_Symbol,_Period,1,needed,m_lows)<needed)
      return(false);

   return(true);
  }

//+------------------------------------------------------------------+
//| One closed bar.                                                  |
//+------------------------------------------------------------------+
void CFpmrStrategy::OnClosedBar(void)
  {
   int required=MathMax(20,PivotRequiredBars(m_cfg.pivotLeftBars,m_cfg.pivotRightBars));

   int totalBars=Bars(_Symbol,_Period);
   if(totalBars<required+2)
      return;

   if(!LoadBarWindow(required))
      return;

   // Absolute, monotonically increasing index of the bar being processed. The
   // forming bar is totalBars-1, so the closed one is totalBars-2.
   m_barIndex=totalBars-2;

   datetime tzBarOpen=m_clock.ToZone(m_barOpenTime,m_sessionTz);

   SessionEvaluation ev;
   m_sessions.Advance(tzBarOpen,ev);

   PumpFairPriceSeries(m_barOpenTime+m_primarySeconds);

   m_fairPrice = m_fair.FairPrice();
   m_hasFair   = m_fair.HasFairPrice();

   // The non-tradeable zone is a percentage of Fair Price, so its width is
   // recomputed each bar from the Fair Price now in force.
   if(m_hasFair)
     {
      double zoneOffset=MathAbs(m_fairPrice)*m_cfg.zonePercent/100.0;
      m_zoneUpper = m_fairPrice+zoneOffset;
      m_zoneLower = m_fairPrice-zoneOffset;
      m_posState  = (m_barClose>m_zoneUpper ? 1 : (m_barClose<m_zoneLower ? -1 : 0));
      m_insideZone= (m_barClose<=m_zoneUpper && m_barClose>=m_zoneLower);
     }
   else
     {
      m_zoneUpper  = FPMR_NA;
      m_zoneLower  = FPMR_NA;
      m_posState   = 0;
      m_insideZone = false;
     }

   ResolveTradingWindow(ev);

   bool sessionStart=(m_effSession!=0 && m_effSession!=m_prevEffSession);
   bool sessionEnd  =(m_effSession==0 && m_prevEffSession!=0);
   m_prevEffSession =m_effSession;

   if(ev.isNewDay)
      m_tradesDay=0;

   // Keyed on the day itself rather than on isNewDay, so the bar loop and a
   // closure detected on the first bar of a new day can never disagree about
   // which day the money belongs to.
   m_trades.RollPnlDay(FpDateOnly(ev.tzBarOpen));

   // Closures first: a trade that hit its stop or target during the bar just
   // closed must be off the books before this bar's gates are evaluated.
   m_trades.SyncClosures(m_barIndex,FpDateOnly(ev.tzBarOpen));
   m_trades.RefreshReconciliation();
   m_trades.ScanAmbiguity(m_barHigh,m_barLow);

   if(sessionStart)
     {
      m_tradesSession   = 0;
      m_tradingStartBar = m_barIndex;
      m_lastSwingHigh   = FPMR_NA;
      m_lastSwingLow    = FPMR_NA;
     }

   m_warmupDone=(m_tradingStartBar>=0 && (m_barIndex-m_tradingStartBar)>=m_cfg.minBarsBeforeFirstTrade);

   m_vwap.OnBar(m_barHigh,m_barLow,m_barClose,m_barVolume,sessionStart || ev.isNewDay);

   //--- Structure, in the Pine call order --------------------------------
   bool fairPriceLost=(!m_hasFair && m_hadFairPrev);
   m_structure.BeginBar(m_posState,sessionStart,fairPriceLost);
   m_hadFairPrev=m_hasFair;

   DetectAndFeedPivots(required);
   m_structure.ExpireSetups(m_barIndex,m_cfg.setupValidityBars);

   double breakUp  =(m_cfg.breakConfirmation==FP_BREAK_CLOSE ? m_barClose : m_barHigh);
   double breakDown=(m_cfg.breakConfirmation==FP_BREAK_CLOSE ? m_barClose : m_barLow);

   StructureBreak brk;
   m_structure.DetectBreak(breakUp,breakDown,m_barIndex,brk);

   if(brk.occurred)
      EvaluateEntry(brk);

   //--- Open trade maintenance -------------------------------------------
   FpTrailContext ctx;
   ctx.high          = m_barHigh;
   ctx.low           = m_barLow;
   ctx.close         = m_barClose;
   ctx.lastSwingHigh = m_lastSwingHigh;
   ctx.lastSwingLow  = m_lastSwingLow;
   ctx.stopBuffer    = m_cfg.stopBufferTicks*m_sym.tickSize;

   m_trades.UpdateTrailingStops(ctx);

   // The zone rule never closes an open trade - only session end can.
   if(sessionEnd && m_cfg.closeAtSessionEnd)
      m_trades.CloseAllOpen("session end");

   m_trades.ApplyDailyLimitFlatten();
  }

//+------------------------------------------------------------------+
//| Which session the strategy considers itself in.                  |
//|                                                                  |
//| Two ways a session's trading can begin from the news candle,      |
//| before the session window itself opens, are wired here in the NT8 |
//| build. Until phase 6 supplies a news resolver no capture ever     |
//| exists, so this resolves to the plain session window.             |
//+------------------------------------------------------------------+
void CFpmrStrategy::ResolveTradingWindow(const SessionEvaluation &ev)
  {
   m_effSession     = ev.index;
   m_inTradingWindow= ev.inSession;

   if(ev.index==0
      && m_fair.IsNewsFairPrice()
      && m_fair.NewsSessionIndex()!=0
      && ev.tzBarOpen<m_fair.NewsSessionOpenTz())
     {
      m_effSession     = m_fair.NewsSessionIndex();
      m_inTradingWindow= true;
     }
  }

//+------------------------------------------------------------------+
//| Advances the Fair Price engine over every reference bar that has  |
//| closed at or before this primary bar.                            |
//|                                                                  |
//| Driven from the primary bar rather than from a second timeframe's |
//| own event, so the result never depends on which series the        |
//| terminal happens to update first.                                 |
//+------------------------------------------------------------------+
void CFpmrStrategy::PumpFairPriceSeries(const datetime primaryCloseTime)
  {
   // The newest reference bar allowed is the last one whose CLOSE is at or
   // before this primary bar's close, i.e. whose OPEN is at or before
   // primaryClose - referenceLength.
   datetime latestAllowedOpen=primaryCloseTime-m_refSeconds;

   if(m_fpCursorTime==0)
     {
      // Seed the cursor far enough back to establish Fair Price for the session
      // this bar belongs to, without replaying the entire history on every start.
      m_fpCursorTime=latestAllowedOpen-(datetime)(3*86400);
     }

   if(latestAllowedOpen<m_fpCursorTime)
      return;

   MqlRates rates[];
   ArraySetAsSeries(rates,false);   // ascending: oldest first

   int n=CopyRates(_Symbol,m_refTf,m_fpCursorTime,latestAllowedOpen,rates);
   if(n<=0)
      return;

   for(int i=0;i<n;i++)
     {
      if(rates[i].time<m_fpCursorTime)
         continue;

      ProcessReferenceBar(rates[i]);
      m_fpCursorTime=rates[i].time+m_refSeconds;
     }
  }

//+------------------------------------------------------------------+
//| One reference bar into the Fair Price engine.                    |
//+------------------------------------------------------------------+
void CFpmrStrategy::ProcessReferenceBar(const MqlRates &r)
  {
   datetime tz=m_clock.ToZone(r.time,m_sessionTz);

   int sessionIndex=m_sessions.IndexAt(tz);
   datetime sessionOpenTz=(sessionIndex==0 ? (datetime)0 : m_sessions.PreviousOpenOf(sessionIndex,tz));

   // Phase 6 fills these in; until then the engine runs the pure session
   // first-candle rule.
   NewsFairPrice capture;  NewsFairPriceClear(capture);
   NewsFairPrice pending;  NewsFairPriceClear(pending);

   m_fair.OnReferenceBar(sessionIndex,sessionOpenTz,m_prevRefSource,capture,pending,tz);

   if(m_fair.ChangedThisBar() && m_cfg.verboseLogging)
      Print(StringFormat("%s  FAIR PRICE %s",
                         TimeToString(tz,TIME_DATE|TIME_MINUTES),
                         DoubleToString(m_fair.FairPrice(),m_sym.digits)));

   // The running previous source is exactly what the C# build read as
   // Opens[idx][barsAgo+1], because reference bars are processed strictly in
   // order and none is ever skipped.
   m_prevRefSource=CFairPriceEngine::SelectSource(m_cfg.fairPriceSource,r.open,r.high,r.low,r.close);
  }

//+------------------------------------------------------------------+
//| Newly CONFIRMED swings.                                          |
//+------------------------------------------------------------------+
void CFpmrStrategy::DetectAndFeedPivots(const int barsAvailable)
  {
   PivotResult ph;
   PivotHigh(m_highs,m_cfg.pivotLeftBars,m_cfg.pivotRightBars,barsAvailable,ph);
   if(ph.found)
     {
      PivotAccepted acc;
      m_structure.OnPivotHigh(ph.price,m_barIndex-ph.barsAgo,m_barIndex,acc);
      m_lastSwingHigh=ph.price;
     }

   PivotResult pl;
   PivotLow(m_lows,m_cfg.pivotLeftBars,m_cfg.pivotRightBars,barsAvailable,pl);
   if(pl.found)
     {
      PivotAccepted acc;
      m_structure.OnPivotLow(pl.price,m_barIndex-pl.barsAgo,m_barIndex,acc);
      m_lastSwingLow=pl.price;
     }
  }

//+------------------------------------------------------------------+
//| Which side the current regime allows.                            |
//|                                                                  |
//| REVERSION (normal mode, and every non-news session): price above  |
//| the Fair Price zone only permits shorts, below it only longs -    |
//| the trade is always back toward Fair Price. CONTINUATION (large   |
//| news surprise, phase 6) treats the move as legitimate repricing   |
//| rather than something to fade, so the rule inverts.               |
//+------------------------------------------------------------------+
bool CFpmrStrategy::SideAllowed(const int dir)
  {
   if(m_fair.NewsBias()==FP_BIAS_CONTINUATION)
      return(dir<0 ? m_posState==-1 : m_posState==1);

   return(dir<0 ? m_posState==1 : m_posState==-1);
  }

//+------------------------------------------------------------------+
//| Entry evaluation                                                 |
//+------------------------------------------------------------------+
void CFpmrStrategy::EvaluateEntry(const StructureBreak &brk)
  {
   int    dir  =brk.direction;
   double entry=m_barClose;

   // Stop: a fixed number of points from the entry, or the displacement
   // candle's extreme. Fixed TP/SL, when on, overrides the structure stop.
   double stop;
   if(m_cfg.useFixedTpSl)
      stop=FpRoundToTick(dir<0 ? entry+m_cfg.fixedStopLossPoints
                               : entry-m_cfg.fixedStopLossPoints, m_sym);
   else
      stop=FpRoundToTick(dir<0 ? m_barHigh+m_cfg.stopBufferTicks*m_sym.tickSize
                               : m_barLow -m_cfg.stopBufferTicks*m_sym.tickSize, m_sym);

   double risk=(dir<0 ? stop-entry : entry-stop);

   // Which distance band the entry sits in. Fixed TP/SL bypasses the band
   // system for both gating (no "too far" limit) and target selection.
   FpSetupBand band=m_cfg.useFixedTpSl
                    ? FP_BAND_NEAR
                    : SetupBandsClassify(entry,m_fairPrice,m_hasFair,
                                         m_cfg.zonePercent,m_cfg.band1Percent,m_cfg.band2Percent);

   SizingResult sizing;
   RiskSizerSize(entry,stop,m_sym.moneyPerPricePerLot,
                 m_cfg.riskTargetUsd,m_cfg.riskToleranceUsd,m_cfg.riskHardCapUsd,
                 m_sym.lotMin,m_sym.lotMax,m_sym.lotStep,m_cfg.maxLots,
                 sizing);

   int openDir=m_trades.OpenCountDirection(dir);

   GateState g;
   GateStateClear(g);

   g.reconciled    = !m_trades.Unreconciled();
   g.inSession     = m_inTradingWindow;
   g.hasFairPrice  = m_hasFair;
   g.newsReady     = true;   // phase 6 supplies the waiting-for-consolidation flag
   g.warmupDone    = m_warmupDone;
   g.inZone        = m_insideZone;
   g.distanceOk    = (band!=FP_BAND_BEYOND);
   g.sideOk        = SideAllowed(dir);
   g.dailyLossOk   = !m_trades.DayLossHit() && m_trades.LossHeadroomFor(sizing.resultingRisk);
   g.dailyProfitOk = !m_trades.DayProfitHit();
   g.dayCapOk      = (m_cfg.maxTradesPerDay==0     || m_tradesDay<m_cfg.maxTradesPerDay);
   g.sessionCapOk  = (m_cfg.maxTradesPerSession==0 || m_tradesSession<m_cfg.maxTradesPerSession);
   g.flatOk        = (!m_cfg.onlyOneOpenTrade || m_trades.OpenCount()==0);
   g.concurrencyOk = (m_cfg.onlyOneOpenTrade || openDir<m_cfg.maxConcurrentEntriesPerDirection);
   g.eventOk       = (brk.event==FP_EVENT_CHOCH ? m_cfg.takeChochEntries : m_cfg.takeBosEntries);
   g.emaOk         = EmaOk(dir>0);
   g.vwapOk        = (dir<0 ? VwapOkShort() : VwapOkLong());
   g.riskOk        = (risk>0.0 && risk>=m_cfg.minStopTicks*m_sym.tickSize);
   g.riskCapOk     = sizing.accepted;
   g.marginOk      = true;

   FpReject reason=RejectionFirstFailure(g);

   if(reason!=FP_REJ_NONE)
     {
      RecordRejection(dir,reason,sizing);
      return;
     }

   SubmitEntry(dir,brk.event,entry,stop,risk,sizing,band);
  }

//+------------------------------------------------------------------+
//| Order submission                                                 |
//+------------------------------------------------------------------+
void CFpmrStrategy::SubmitEntry(const int dir,const FpBreakEvent evt,const double entry,
                                const double stop,const double risk,const SizingResult &sizing,
                                const FpSetupBand band)
  {
   // Take-profit selection:
   //   Fixed TP/SL on -> a fixed number of points from the entry.
   //   FAR band       -> Fair Price itself (target the full reversion).
   //   NEAR band      -> the Band 1 risk/reward multiple.
   double rawTarget;
   bool   targetIsFair;

   if(m_cfg.useFixedTpSl)
     {
      rawTarget   =(dir<0 ? entry-m_cfg.fixedTakeProfitPoints : entry+m_cfg.fixedTakeProfitPoints);
      targetIsFair=false;
     }
   else if(band==FP_BAND_FAR)
     {
      rawTarget   =m_fairPrice;
      targetIsFair=true;
     }
   else
     {
      rawTarget   =(dir<0 ? entry-risk*m_cfg.rewardRatio : entry+risk*m_cfg.rewardRatio);
      targetIsFair=false;
     }

   double target=FpRoundToTick(rawTarget,m_sym);

   // After rounding the target must still sit at least one tick beyond the
   // signal price, otherwise the bracket is nonsense.
   bool targetViable=(dir<0 ? target<=entry-m_sym.tickSize : target>=entry+m_sym.tickSize);
   if(!targetViable)
     {
      RecordRejection(dir,FP_REJ_RISK,sizing);
      return;
     }

   FpTrailMode trail=(m_cfg.useFixedTpSl ? FP_TRAIL_OFF : m_cfg.trailMode);

   string error="";
   if(!m_trades.Submit(dir,evt,entry,stop,target,targetIsFair,trail,sizing,
                       m_barIndex,m_barOpenTime,m_effSession,error))
     {
      m_lastRejectText="BROKER";
      Print(StringFormat("%s  ENTRY REFUSED (%s displacement) - %s",
                         TimeToString(m_barOpenTime,TIME_DATE|TIME_SECONDS),
                         dir<0 ? "bearish" : "bullish", error));
      return;
     }

   m_tradesDay++;
   m_tradesSession++;

   Print(StringFormat("%s  ENTRY %s %s #%d %s lots @ %s | SL %s | TP %s%s  |  %s",
                      TimeToString(m_barOpenTime,TIME_DATE|TIME_SECONDS),
                      dir>0 ? "LONG" : "SHORT",
                      FpBreakEventText(evt),
                      m_trades.TradeSequence(),
                      DoubleToString(sizing.lots,m_sym.lotDigits),
                      DoubleToString(entry,m_sym.digits),
                      DoubleToString(stop,m_sym.digits),
                      DoubleToString(target,m_sym.digits),
                      targetIsFair ? " | FP-target" : "",
                      SizingDescribe(sizing,m_cfg.riskTargetUsd,m_cfg.riskToleranceUsd,
                                     m_cfg.riskHardCapUsd,m_sym.lotStep)));
  }

//+------------------------------------------------------------------+
//| Filters and diagnostics                                          |
//+------------------------------------------------------------------+

//--- EMA gate. Each leg is independent:
//      both legs on  -> crossover rule, long needs EMA1 above EMA2;
//      one leg on    -> price-vs-EMA rule, long needs the close above that EMA;
//      no leg on     -> no constraint, same as the master switch being off.
//    A disabled leg has an invalid handle, so the shape of the test follows what
//    was built. A handle that cannot be read yet passes rather than blocks, the
//    same way an unfilled NinjaTrader indicator series would have.
bool CFpmrStrategy::EmaOk(const bool isLong)
  {
   if(!m_cfg.useEmaFilter)
      return(true);

   bool one=(m_emaFastHandle!=INVALID_HANDLE);
   bool two=(m_emaSlowHandle!=INVALID_HANDLE);

   double f[1], s[1];

   if(one && CopyBuffer(m_emaFastHandle,0,1,1,f)<1) return(true);
   if(two && CopyBuffer(m_emaSlowHandle,0,1,1,s)<1) return(true);

   if(one && two)
      return(isLong ? f[0]>s[0] : f[0]<s[0]);

   if(one)
      return(isLong ? m_barClose>f[0] : m_barClose<f[0]);

   if(two)
      return(isLong ? m_barClose>s[0] : m_barClose<s[0]);

   return(true);
  }

bool CFpmrStrategy::VwapOkLong(void)
  {
   return(!m_cfg.useVwapFilter || !m_vwap.HasValue() || m_barClose>m_vwap.Value());
  }

bool CFpmrStrategy::VwapOkShort(void)
  {
   return(!m_cfg.useVwapFilter || !m_vwap.HasValue() || m_barClose<m_vwap.Value());
  }

void CFpmrStrategy::RecordRejection(const int dir,const FpReject reason,const SizingResult &sizing)
  {
   m_lastRejectText=RejectionLabel(reason);

   if(!m_cfg.verboseLogging)
      return;

   string extra="";
   if(reason==FP_REJ_RISK_CAP || reason==FP_REJ_RISK)
      extra="  "+SizingDescribe(sizing,m_cfg.riskTargetUsd,m_cfg.riskToleranceUsd,
                                m_cfg.riskHardCapUsd,m_sym.lotStep);

   Print(StringFormat("%s  REJECT %s (%s displacement)%s",
                      TimeToString(m_barOpenTime,TIME_DATE|TIME_SECONDS),
                      m_lastRejectText,
                      dir<0 ? "bearish" : "bullish",
                      extra));
  }

#endif // FPMR_STRATEGY_IMPL_MQH
//+------------------------------------------------------------------+
