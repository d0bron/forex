using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class FVG_HighestHigh_OverlapBot : Robot
    {
        #region Risk Management Parameters
        [Parameter("Risk per Trade (%)", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.1)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Reward Ratio (TP)", Group = "Risk Management", DefaultValue = 2.0)]
        public double RewardRatio { get; set; }
        #endregion

        #region H4 Entry Parameters
        [Parameter("H4 Lookback (Overlap)", Group = "H4 Entry Logic", DefaultValue = 20, MinValue = 1)]
        public int Lookback { get; set; }

        [Parameter("H4 Swing Lookback (SL)", Group = "H4 Entry Logic", DefaultValue = 5, MinValue = 1)]
        public int SwingLookback { get; set; }

        [Parameter("Ignoriere Datum (H4 Swing High)", Group = "H4 Entry Logic", DefaultValue = "2026-07-30")]
        public string IgnoreDateString { get; set; }
        #endregion

        #region Weekly Context Parameters
        [Parameter("Weekly Swing Lookback", Group = "Weekly Context", DefaultValue = 3, MinValue = 1)]
        public int WeeklySwingLookback { get; set; }
        #endregion

        private int _lastFvgBar = -1;
        private DateTime? _ignoreDate;

        // Weekly Timeframe Multi-Timeframe Series
        private Bars _weeklyBars;
        private bool _weeklyContextActive = false;
        private double _weeklyTargetHigh = double.MaxValue;
        private double _weeklySwingLow = double.MinValue;

        protected override void OnStart()
        {
            // Parse das zu ignorierende Datum
            if (DateTime.TryParse(IgnoreDateString, out DateTime parsedDate))
            {
                _ignoreDate = parsedDate.Date;
                Print("📅 Kerzen vom Datum {0} werden beim H4 Swing High ignoriert.", _ignoreDate.Value.ToString("yyyy-MM-dd"));
            }

            // Hole native Wochen-Daten direkt vom Server
            _weeklyBars = MarketData.GetBars(TimeFrame.Weekly);
        }

        protected override void OnBar()
        {
            int i = Bars.Count - 1;
            if (i < 30) return; // Genügend Historie sicherstellen

            // =========================================================================
            // SCHRITT 1: WEEKLY CONTEXT UPDATE & LIQUIDITY SWEEP PRÜFUNG (H4 ECHTZEIT)
            // =========================================================================
            UpdateWeeklyLevels();

            double currentH4Low = Bars.LowPrices[i];
            double currentH4High = Bars.HighPrices[i];

            // A) Sweep-Erkennung: H4 taucht unter das relevante Weekly Swing Low
            if (!_weeklyContextActive && _weeklySwingLow > 0 && currentH4Low < _weeklySwingLow)
            {
                _weeklyContextActive = true;
                Print("🔥 WEEKLY SWEEP BESTÄTIGT! H4-Tief ({0}) hat Weekly Low ({1}) durchbrochen. Kontext = BULLISCH.", currentH4Low, _weeklySwingLow);
                Chart.DrawHorizontalLine("Weekly_Target_" + i, _weeklyTargetHigh, Color.Lime, 2, LineStyle.DotsVeryRare);
            }

            // B) Target erreicht: H4 erreicht/übersteigt das Weekly Target High -> Kontext deaktivieren
            if (_weeklyContextActive && currentH4High >= _weeklyTargetHigh)
            {
                _weeklyContextActive = false;
                Print("🎯 WEEKLY TARGET ERREICHT! H4-Hoch ({0}) hat Weekly Target ({1}) getroffen. Suche gestoppt.", currentH4High, _weeklyTargetHigh);
                return;
            }

            // Wenn der Weekly Kontext nicht aktiv ist, brechen wir ab (keine Trades außerhalb der Zone)
            if (!_weeklyContextActive)
            {
                return;
            }

            // =========================================================================
            // SCHRITT 2: H4 ENTRY SETUP (FVG + SWING HIGH OVERLAP)
            // =========================================================================
            
            // 1. Bullische FVG-Erkennung (3-Kerzen-Muster im H4 Chart)
            double gapBottom = Bars.HighPrices[i - 3];
            double gapTop = Bars.LowPrices[i - 1];

            if (gapBottom < gapTop && _lastFvgBar != i)
            {
                // 2. Swing High Suche vor dem Gap
                double swingHighPrice = -1;
                bool swingFound = false;

                for (int j = i - 4; j >= i - 4 - Lookback && j >= 2; j--)
                {
                    // Filter für ignoriertes Datum
                    if (_ignoreDate.HasValue && Bars.OpenTimes[j].Date == _ignoreDate.Value)
                    {
                        Print("⚠️ H4 Swing High am {0} wird ignoriert.", Bars.OpenTimes[j].ToString("yyyy-MM-dd"));
                        continue;
                    }

                    // Exakte Swing High Definition im H4
                    if (Bars.HighPrices[j] > Bars.HighPrices[j - 1] && 
                        Bars.HighPrices[j] > Bars.HighPrices[j + 1])
                    {
                        swingHighPrice = Bars.HighPrices[j];
                        swingFound = true;
                        break; // Nimm das frischeste gültige Swing High
                    }
                }

                // 3. Einstiegs-Logik (Overlap im FVG)
                bool validSetup = false;
                double entryPrice = 0;

                if (swingFound)
                {
                    // Prüfen, ob das Swing High exakt im FVG-Bereich liegt
                    if (swingHighPrice >= gapBottom && swingHighPrice <= gapTop)
                    {
                        entryPrice = swingHighPrice;
                        validSetup = true;
                        Print("🎯 H4 Entry gefunden: Entry bei {0} (FVG: {1} - {2}) im Weekly Context", entryPrice, gapBottom, gapTop);
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
                if (Positions.Any(p => p.Label == "FVG_Overlap" && p.SymbolName == SymbolName))
                    return;

                // 6. Cancel & Replace: Vorherige schwebende Orders löschen
                var existingOrders = PendingOrders.Where(o => o.Label == "FVG_Overlap" && o.SymbolName == SymbolName).ToList();
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
                        PlaceLimitOrder(TradeType.Buy, SymbolName, volume, entryPrice, "FVG_Overlap", slPips, tpPips);
                        _lastFvgBar = i;

                        // Visualisierung im Chart
                        Chart.DrawHorizontalLine("Entry_PD_" + i, entryPrice, Color.Gold, 2);
                        Chart.DrawHorizontalLine("SL_" + i, slPrice, Color.Red, 1, LineStyle.Lines);
                    }
                }
            }
        }

        // =========================================================================
        // HILFSMETHODEN
        // =========================================================================

        // Ermittelt die relevanten Weekly Levels (Swing Low & Target High)
        private void UpdateWeeklyLevels()
        {
            if (_weeklyBars == null || _weeklyBars.Count < 10) return;

            int wIndex = _weeklyBars.Count - 2; // Letzte geschlossene Weekly-Kerze

            // Suche nach dem letzten Weekly Swing Low
            for (int w = wIndex; w >= wIndex - 15 && w >= WeeklySwingLookback; w--)
            {
                bool isSwingLow = true;
                for (int k = 1; k <= WeeklySwingLookback; k++)
                {
                    if (_weeklyBars.LowPrices[w] >= _weeklyBars.LowPrices[w - k] ||
                        _weeklyBars.LowPrices[w] >= _weeklyBars.LowPrices[w + k])
                    {
                        isSwingLow = false;
                        break;
                    }
                }

                if (isSwingLow)
                {
                    _weeklySwingLow = _weeklyBars.LowPrices[w];

                    // Finde das zugehörige Target High (Höchstes Hoch nach/vor dem Swing)
                    double highestHigh = double.MinValue;
                    for (int h = w; h <= wIndex; h++)
                    {
                        if (_weeklyBars.HighPrices[h] > highestHigh)
                            highestHigh = _weeklyBars.HighPrices[h];
                    }

                    if (highestHigh > _weeklySwingLow)
                        _weeklyTargetHigh = highestHigh;

                    break;
                }
            }
        }

        // Ermittelt das tiefste Kerzenkörper-Tief der letzten Swings für den H4 SL
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
