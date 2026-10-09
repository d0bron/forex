using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robotsx
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class FVG_Monthly_Daily_Bot : Robots
    {
        #region Risk Management Parameters
        [Parameter("Risk per Trade (%)", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.1)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Reward Ratio (TP)", Group = "Risk Management", DefaultValue = 2.0)]
        public double RewardRatio { get; set; }
        #endregion

        #region Daily Entry Parameters
        [Parameter("Daily Lookback (Overlap)", Group = "Daily Entry Logic", DefaultValue = 20, MinValue = 1)]
        public int Lookback { get; set; }

        [Parameter("Daily Swing Lookback (SL)", Group = "Daily Entry Logic", DefaultValue = 5, MinValue = 1)]
        public int SwingLookback { get; set; }

        [Parameter("Ignoriere Datum (Daily Swing High)", Group = "Daily Entry Logic", DefaultValue = "2026-07-30")]
        public string IgnoreDateString { get; set; }
        #endregion

        #region Monthly Context Parameters
        [Parameter("Monthly Swing Lookback", Group = "Monthly Context", DefaultValue = 3, MinValue = 1)]
        public int MonthlySwingLookback { get; set; }
        #endregion

        private int _lastFvgBar = -1;
        private DateTime? _ignoreDate;

        // Monthly Timeframe Multi-Timeframe Series
        private Bars _monthlyBars;
        private bool _monthlyContextActive = false;
        private double _monthlyTargetHigh = double.MaxValue;
        private double _monthlySwingLow = double.MinValue;

        protected override void OnStart()
        {
            // Parse das zu ignorierende Datum
            if (DateTime.TryParse(IgnoreDateString, out DateTime parsedDate))
            {
                _ignoreDate = parsedDate.Date;
                Print("📅 Kerzen vom Datum {0} werden beim Daily Swing High ignoriert.", _ignoreDate.Value.ToString("yyyy-MM-dd"));
            }

            // Hole native Monats-Daten direkt vom Server
            _monthlyBars = MarketData.GetBars(TimeFrame.Monthly);
        }

        protected override void OnBar()
        {
            int i = Bars.Count - 1;
            if (i < 30) return; // Genügend Historie sicherstellen

            // =========================================================================
            // SCHRITT 1: MONTHLY CONTEXT UPDATE & LIQUIDITY SWEEP PRÜFUNG (DAILY)
            // =========================================================================
            UpdateMonthlyLevels();

            double currentDailyLow = Bars.LowPrices[i];
            double currentDailyHigh = Bars.HighPrices[i];

            // A) Sweep-Erkennung: Daily taucht unter das relevante Monthly Swing Low
            if (!_monthlyContextActive && _monthlySwingLow > 0 && currentDailyLow < _monthlySwingLow)
            {
                _monthlyContextActive = true;
                Print("🔥 MONTHLY SWEEP BESTÄTIGT! Daily-Tief ({0}) hat Monthly Low ({1}) durchbrochen. Kontext = BULLISCH.", currentDailyLow, _monthlySwingLow);
                Chart.DrawHorizontalLine("Monthly_Target_" + i, _monthlyTargetHigh, Color.Lime, 2, LineStyle.DotsVeryRare);
            }

            // B) Target erreicht: Daily erreicht/übersteigt das Monthly Target High -> Kontext deaktivieren
            if (_monthlyContextActive && currentDailyHigh >= _monthlyTargetHigh)
            {
                _monthlyContextActive = false;
                Print("🎯 MONTHLY TARGET ERREICHT! Daily-Hoch ({0}) hat Monthly Target ({1}) getroffen. Suche gestoppt.", currentDailyHigh, _monthlyTargetHigh);
                return;
            }

            // Wenn der Monthly Kontext nicht aktiv ist, brechen wir ab
            if (!_monthlyContextActive)
            {
                return;
            }

            // =========================================================================
            // SCHRITT 2: DAILY ENTRY SETUP (FVG + SWING HIGH BODY OVERLAP)
            // =========================================================================
            
            // 1. Bullische FVG-Erkennung (3-Kerzen-Muster im Daily Chart)
            double gapBottom = Bars.HighPrices[i - 3];
            double gapTop = Bars.LowPrices[i - 1];

            if (gapBottom < gapTop && _lastFvgBar != i)
            {
                // 2. Swing High Body Suche vor dem Gap (auf dem Daily Chart)
                double swingHighBodyPrice = -1;
                bool swingFound = false;

                for (int j = i - 4; j >= i - 4 - Lookback && j >= 2; j--)
                {
                    // Filter für ignoriertes Datum
                    if (_ignoreDate.HasValue && Bars.OpenTimes[j].Date == _ignoreDate.Value)
                    {
                        Print("⚠️ Daily Swing High am {0} wird ignoriert.", Bars.OpenTimes[j].ToString("yyyy-MM-dd"));
                        continue;
                    }

                    // Berechnung der Kerzenkörper-Höhen
                    double currentBodyHigh = Math.Max(Bars.OpenPrices[j], Bars.ClosePrices[j]);
                    double prevBodyHigh = Math.Max(Bars.OpenPrices[j - 1], Bars.ClosePrices[j - 1]);
                    double nextBodyHigh = Math.Max(Bars.OpenPrices[j + 1], Bars.ClosePrices[j + 1]);

                    // Swing High Definition basierend auf dem BODY
                    if (currentBodyHigh > prevBodyHigh && currentBodyHigh > nextBodyHigh)
                    {
                        swingHighBodyPrice = currentBodyHigh;
                        swingFound = true;
                        break; // Nimm das frischeste gültige Swing High Body
                    }
                }

                // 3. Einstiegs-Logik (Body Overlap im FVG)
                bool validSetup = false;
                double entryPrice = 0;

                if (swingFound)
                {
                    // Prüfen, ob das Swing High BODY exakt im FVG-Bereich liegt
                    if (swingHighBodyPrice >= gapBottom && swingHighBodyPrice <= gapTop)
                    {
                        entryPrice = swingHighBodyPrice; // Entry genau auf Höhe des Kerzenkörpers
                        validSetup = true;
                        Print("🎯 Daily Entry gefunden: Entry-Limit am Body bei {0} (FVG: {1} - {2})", entryPrice, gapBottom, gapTop);
                    }
                }

                if (!validSetup)
                    return;

                // 4. Stop Loss & Take Profit ermitteln
                double slPrice = GetLastSwingLowBody(i) - (2 * Symbol.PipSize);
                if (slPrice >= entryPrice || slPrice <= 0) return;

                double slPips = (entryPrice - slPrice) / Symbol.PipSize;
                double tpPips = slPips * RewardRatio;

                // 5. Keine neue Order setzen, wenn bereits eine marktaktive Position läuft
                if (Positions.Any(p => p.Label == "FVG_Monthly_Daily" && p.SymbolName == SymbolName))
                    return;

                // 6. Cancel & Replace: Vorherige schwebende Orders löschen
                var existingOrders = PendingOrders.Where(o => o.Label == "FVG_Monthly_Daily" && o.SymbolName == SymbolName).ToList();
                foreach (var order in existingOrders)
                {
                    CancelPendingOrder(order);
                    Print("🗑️ Alte Pending Order {0} gelöscht für neuen Einstieg.", order.Id);
                }

                // 7. Risikoberechnung & Platzierung der neuen Order
                double riskAmount = Account.Balance * (RiskPerTradePercent / 100.0);
                double riskPerVolumeUnit = slPips * Symbol.PipValue;

                if (riskPerVolumeUnit > 0)
                {
                    double rawVolume = riskAmount / riskPerVolumeUnit;
                    double volume = Symbol.NormalizeVolumeInUnits(rawVolume);

                    if (volume >= Symbol.VolumeInUnitsMin)
                    {
                        PlaceLimitOrder(TradeType.Buy, SymbolName, volume, entryPrice, "FVG_Monthly_Daily", slPips, tpPips);
                        _lastFvgBar = i;

                        // Visualisierung im Chart
                        Chart.DrawHorizontalLine("Entry_Body_" + i, entryPrice, Color.Gold, 2);
                        Chart.DrawHorizontalLine("SL_" + i, slPrice, Color.Red, 1, LineStyle.Lines);
                    }
                }
            }
        }

        // =========================================================================
        // HILFSMETHODEN
        // =========================================================================

        // Ermittelt die relevanten Monthly Levels (Swing Low & Target High)
        private void UpdateMonthlyLevels()
        {
            if (_monthlyBars == null || _monthlyBars.Count < 10) return;

            int mIndex = _monthlyBars.Count - 2; // Letzte geschlossene Monats-Kerze

            // Suche nach dem letzten Monthly Swing Low
            for (int m = mIndex; m >= mIndex - 15 && m >= MonthlySwingLookback; m--)
            {
                bool isSwingLow = true;
                for (int k = 1; k <= MonthlySwingLookback; k++)
                {
                    if (_monthlyBars.LowPrices[m] >= _monthlyBars.LowPrices[m - k] ||
                        _monthlyBars.LowPrices[m] >= _monthlyBars.LowPrices[m + k])
                    {
                        isSwingLow = false;
                        break;
                    }
                }

                if (isSwingLow)
                {
                    _monthlySwingLow = _monthlyBars.LowPrices[m];

                    // Finde das zugehörige Target High (Höchstes Hoch nach/vor dem Swing)
                    double highestHigh = double.MinValue;
                    for (int h = m; h <= mIndex; h++)
                    {
                        if (_monthlyBars.HighPrices[h] > highestHigh)
                            highestHigh = _monthlyBars.HighPrices[h];
                    }

                    if (highestHigh > _monthlySwingLow)
                        _monthlyTargetHigh = highestHigh;

                    break;
                }
            }
        }

        // Ermittelt das tiefste Kerzenkörper-Tief der letzten Swings für den Daily SL
        private double GetLastSwingLowBody(int fromIndex)
        {
            int minIndex = Math.Max(0, fromIndex - 30);
            for (int j = fromIndex - 2; j >= minIndex; j--)
            {
                double bodyLow = Math.Min(Bars.OpenPrices[j], Bars.ClosePrices[j]);
                bool isSwing = true;

                for (int k = 1; k <= SwingLookback; k++)
                {
                    if (j - k < 0)
                    {
                        isSwing = false;
                        break;
                    }
                    double neighborLow = Math.Min(Bars.OpenPrices[j - k], Bars.ClosePrices[j - k]);
                    if (bodyLow > neighborLow)
                    {
                        isSwing = false;
                        break;
                    }
                }

                if (isSwing) return bodyLow;
            }

            return Bars.LowPrices[fromIndex - 3]; // Fallback
        }
    }
}
