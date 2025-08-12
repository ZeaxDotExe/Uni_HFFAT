using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using System.IO; // Required for file operations
using System.Text; // Required for StringBuilder

namespace cAlgo
{
    // *** IMPORTANT: Changed AccessRights to FullAccess to allow file writing ***
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class SmartGrid : Robot
    {
        [Parameter("Buy", DefaultValue = true)]
        public bool Buy { get; set; }

        [Parameter("Sell", DefaultValue = true)]
        public bool boolSell { get; set; }

        [Parameter("Pip Step", DefaultValue = 10, MinValue = 1)]
        public int PipStep { get; set; }

        [Parameter("First Volume", DefaultValue = 1000, MinValue = 1000, Step = 1000)]
        public int FirstVolume { get; set; }

        [Parameter("Volume Exponent", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0)]
        public double VolumeExponent { get; set; }

        [Parameter("Max Spread", DefaultValue = 3.0)]
        public double MaxSpread { get; set; }

        [Parameter("Average TP", DefaultValue = 3, MinValue = 1)]
        public int AverageTP { get; set; }

        // --- PARAMETERS FOR END OF DAY CLOSURE ---
        [Parameter("Close at End of Day", DefaultValue = true)]
        public bool CloseAtEndOfDay { get; set; }

        [Parameter("End Day Hour (UTC)", DefaultValue = 23, MinValue = 0, MaxValue = 23)]
        public int EndDayHourUTC { get; set; }

        [Parameter("End Day Minute (UTC)", DefaultValue = 59, MinValue = 0, MaxValue = 59)]
        public int EndDayMinuteUTC { get; set; }
        // --- END PARAMETERS ---

        // --- PARAMETERS FOR DYNAMIC PIP STEP ---
        [Parameter("Dynamic Pip Step (ATR)", DefaultValue = false)]
        public bool UseDynamicPipStep { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1)]
        public int AtrPeriod { get; set; }

        [Parameter("ATR Multiplier", DefaultValue = 1.0, MinValue = 0.1)]
        public double AtrMultiplier { get; set; }
        // --- END PARAMETERS ---

        // --- PARAMETERS FOR TREND FILTER ---
        [Parameter("Enable Trend Filter (MA)", DefaultValue = false)]
        public bool EnableTrendFilter { get; set; }

        [Parameter("Trend MA Period", DefaultValue = 100, MinValue = 10)]
        public int TrendMAPeriod { get; set; }

        [Parameter("Trend MA Type", DefaultValue = MovingAverageType.Simple)]
        public MovingAverageType TrendMAType { get; set; }
        // --- END NEW PARAMETERS ---

        // --- NEW PARAMETERS FOR CSV LOGGING ---
        [Parameter("Enable Daily Profit CSV Log", DefaultValue = true)]
        public bool EnableDailyProfitLog { get; set; }

        // *** NEW: Parameter for the CSV File Path ***
        [Parameter("Daily Profit CSV Path", DefaultValue = "C:\\cBots\\DailyProfit.csv")]
        public string DailyProfitCsvPath { get; set; }
        // --- END NEW PARAMETERS ---

        private string Label = "cls";
        private Position position;
        private DateTime tc_31;
        private DateTime tc_32;
        private int gi_21;
        private double sp_d;
        private bool is_12 = true;
        private bool cStop = false;

        private DateTime _lastDailyCloseTime;
        private AverageTrueRange _atr;
        private MovingAverage _trendMA;

        private DateTime _lastDailyRecordDate; // To track when the last daily profit was recorded
        private double _previousBalance; // To calculate daily profit


        protected override void OnStart()
        {
            _lastDailyCloseTime = Server.Time.Date.AddDays(-1);

            _atr = Indicators.AverageTrueRange(MarketSeries, AtrPeriod, MovingAverageType.Simple);
            _trendMA = Indicators.MovingAverage(MarketSeries.Close, TrendMAPeriod, TrendMAType);

            // --- Initialize for CSV Logging ---
            if (EnableDailyProfitLog)
            {
                // Ensure the directory exists
                string directory = Path.GetDirectoryName(DailyProfitCsvPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    try
                    {
                        Directory.CreateDirectory(directory);
                        Print($"Created directory: {directory}");
                    }
                    catch (Exception ex)
                    {
                        Print($"Error creating directory {directory}: {ex.Message}");
                        EnableDailyProfitLog = false; // Disable logging if directory cannot be created
                        return;
                    }
                }

                // Load previous balance using LocalStorage (still good for persistent internal state)
                string lastBalanceKey = $"LastBalance_{Symbol.Code}_{InstanceId}"; // Use InstanceId to differentiate across bot instances
                string lastBalanceStr = LocalStorage.GetString(lastBalanceKey);
                
                if (double.TryParse(lastBalanceStr, out _previousBalance))
                {
                    Print($"Loaded previous balance: {_previousBalance}");
                }
                else
                {
                    _previousBalance = Account.Balance;
                    LocalStorage.SetString(lastBalanceKey, _previousBalance.ToString());
                    Print($"Initialized previous balance: {_previousBalance}");
                }

                // Ensure header is written if file is new or empty
                try
                {
                    if (!File.Exists(DailyProfitCsvPath) || new FileInfo(DailyProfitCsvPath).Length == 0)
                    {
                        // *** CHANGED: Use semicolon as separator for header ***
                        string header = "Date;DailyProfit";
                        File.AppendAllText(DailyProfitCsvPath, header + Environment.NewLine);
                        Print("CSV Header written to file.");
                    }
                }
                catch (Exception ex)
                {
                    Print($"Error writing CSV header to {DailyProfitCsvPath}: {ex.Message}");
                    EnableDailyProfitLog = false; // Disable logging if file cannot be accessed
                    return;
                }

                // Initialize _lastDailyRecordDate from file or to current date if no records
                try
                {
                    if (File.Exists(DailyProfitCsvPath))
                    {
                        string[] lines = File.ReadAllLines(DailyProfitCsvPath);
                        if (lines.Length > 1) // Header + at least one data row
                        {
                            string lastLine = lines.Last();
                            // *** CHANGED: Split by semicolon for reading last date ***
                            string[] parts = lastLine.Split(';');
                            if (parts.Length >= 1 && DateTime.TryParse(parts[0], out DateTime lastDateFromFile))
                            {
                                _lastDailyRecordDate = lastDateFromFile;
                                Print($"Last recorded daily profit date from file: {_lastDailyRecordDate.ToString("yyyy-MM-dd")}");
                            }
                            else
                            {
                                _lastDailyRecordDate = Server.Time.Date; // Fallback if parsing fails
                                Print("Could not parse last recorded date from CSV. Initializing to current date.");
                            }
                        }
                        else
                        {
                            _lastDailyRecordDate = Server.Time.Date; // Only header, or empty file after header write
                            Print("CSV file has only header or is empty. Initializing last recorded date to current date.");
                        }
                    }
                    else
                    {
                        _lastDailyRecordDate = Server.Time.Date;
                        Print("CSV file does not exist (after initial check). Initializing last recorded date to current date.");
                    }
                }
                catch (Exception ex)
                {
                    Print($"Error reading existing CSV data from {DailyProfitCsvPath}: {ex.Message}");
                    _lastDailyRecordDate = Server.Time.Date; // Fallback
                }
            }
            // --- End CSV Logging Initialization ---
        }

        protected override void OnTick()
        {
            sp_d = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;

            if (o_tm(TradeType.Buy) > 0)
                f0_86(pnt_12(TradeType.Buy), AverageTP);
            if (o_tm(TradeType.Sell) > 0)
                f0_88(pnt_12(TradeType.Sell), AverageTP);

            bool isUptrend = false;
            bool isDowntrend = false;

            if (EnableTrendFilter)
            {
                double maValue = _trendMA.Result.LastValue;

                if (Symbol.Ask > maValue)
                {
                    isUptrend = true;
                }
                else if (Symbol.Bid < maValue)
                {
                    isDowntrend = true;
                }
            }

            if (MaxSpread >= sp_d && !cStop)
                Open_24(isUptrend, isDowntrend);

            RCN();

            // End of Day Closure Logic
            if (CloseAtEndOfDay)
            {
                DateTime currentUTC = Server.Time;
                DateTime endOfDayCloseTarget = currentUTC.Date.AddHours(EndDayHourUTC).AddMinutes(EndDayMinuteUTC);

                if (currentUTC >= endOfDayCloseTarget && _lastDailyCloseTime.Date < currentUTC.Date)
                {
                    CloseAllBotPositions();
                    _lastDailyCloseTime = currentUTC;
                }
            }
        }

        protected override void OnError(Error error)
        {
            if (error.Code == ErrorCode.NoMoney)
            {
                cStop = true;
                Print("Opening stopped because: not enough money");
            }
        }

        protected override void OnBar()
        {
            RefreshData();

            // --- Daily Profit Logging Check on new Daily Bar ---
            if (EnableDailyProfitLog)
            {
                // We'll check Server.Time.Date for a new day.
                // It's important to record for the *previous* day when a *new* day bar starts.
                if (Server.Time.Date > _lastDailyRecordDate)
                {
                    // Calculate and save for the day that just ended
                    CalculateAndSaveDailyProfit(Server.Time.Date.AddDays(-1));
                    _lastDailyRecordDate = Server.Time.Date; // Update the last recorded date to the current day
                }
            }
        }

        protected override void OnStop()
        {
            ChartObjects.RemoveAllObjects();

            Print("cBot is stopping. Closing all active positions...");
            CloseAllBotPositions();

            // --- Save final daily profit if stopping mid-day ---
            if (EnableDailyProfitLog)
            {
                 // Only record if current day has not been recorded yet or if it's a new day since last record.
                if (Server.Time.Date > _lastDailyRecordDate || Account.Balance != _previousBalance)
                {
                    CalculateAndSaveDailyProfit(Server.Time.Date); // Record for the current (possibly partial) day
                }

                // Save current balance for next start (using InstanceId for uniqueness)
                LocalStorage.SetString($"LastBalance_{Symbol.Code}_{InstanceId}", Account.Balance.ToString());
                Print($"Final balance saved: {Account.Balance}");
            }
        }

        private void Open_24(bool isUptrend, bool isDowntrend)
        {
            if (is_12)
            {
                if (Buy && o_tm(TradeType.Buy) == 0 && MarketSeries.Close.Last(1) > MarketSeries.Close.Last(2) && (!EnableTrendFilter || isUptrend))
                {
                    gi_21 = OrderSend(TradeType.Buy, fer(FirstVolume, 0));
                    if (gi_21 > 0)
                        tc_31 = MarketSeries.OpenTime.Last(0);
                    else
                        Print("First BUY opening error at: ", Symbol.Ask, "Error Type: ", LastResult.Error);
                }
                if (boolSell && o_tm(TradeType.Sell) == 0 && MarketSeries.Close.Last(2) > MarketSeries.Close.Last(1) && (!EnableTrendFilter || isDowntrend))
                {
                    gi_21 = OrderSend(TradeType.Sell, fer(FirstVolume, 0));
                    if (gi_21 > 0)
                        tc_32 = MarketSeries.OpenTime.Last(0);
                    else
                        Print("First SELL opening error at: ", Symbol.Bid, "Error Type: ", LastResult.Error);
                }
            }
            N_28(isUptrend, isDowntrend);
        }

        private void N_28(bool isUptrend, bool isDowntrend)
        {
            double currentPipStep = PipStep;

            if (UseDynamicPipStep)
            {

                double atrInPips = _atr.Result.LastValue / Symbol.PipSize;
                currentPipStep = atrInPips * AtrMultiplier;
                if (currentPipStep < 1)
                    currentPipStep = 1;
            }

            if (o_tm(TradeType.Buy) > 0)
            {
                if (!EnableTrendFilter || isUptrend)
                {
                    if (Math.Round(Symbol.Ask, Symbol.Digits) < Math.Round(D_TD(TradeType.Buy) - currentPipStep * Symbol.PipSize, Symbol.Digits) && tc_31 != MarketSeries.OpenTime.Last(0))
                    {
                        long gl_57 = n_lt(TradeType.Buy);
                        gi_21 = OrderSend(TradeType.Buy, fer(gl_57, 2));
                        if (gi_21 > 0)
                            tc_31 = MarketSeries.OpenTime.Last(0);
                        else
                            Print("Next BUY opening error at: ", Symbol.Ask, "Error Type: ", LastResult.Error);
                    }
                }
            }

            if (o_tm(TradeType.Sell) > 0)
            {
                if (!EnableTrendFilter || isDowntrend)
                {
                    if (Math.Round(Symbol.Bid, Symbol.Digits) > Math.Round(U_TD(TradeType.Sell) + currentPipStep * Symbol.PipSize, Symbol.Digits))
                    {
                        long gl_59 = n_lt(TradeType.Sell);
                        gi_21 = OrderSend(TradeType.Sell, fer(gl_59, 2));
                        if (gi_21 > 0)
                            tc_32 = MarketSeries.OpenTime.Last(0);
                        else
                            Print("Next SELL opening error at: ", Symbol.Bid, "Error Type: ", LastResult.Error);
                    }
                }
            }
        }

        private int OrderSend(TradeType TrdTp, long iVol)
        {
            int cd_8 = 0;
            if (iVol > 0)
            {
                TradeResult result = ExecuteMarketOrder(TrdTp, Symbol, iVol, Label, 0, 0, 0, "smart_grid");

                if (result.IsSuccessful)
                {
                    Print(TrdTp, " Opened at: ", result.Position.EntryPrice, " Volume: ", result.Position.Volume);
                    cd_8 = 1;
                }
                else
                    Print(TrdTp, " Opening Error: ", result.Error);
            }
            else
                Print("Volume calculation error: Calculated Volume is: ", iVol);
            return cd_8;
        }

        private void f0_86(double ai_4, int ad_8)
        {
            foreach (var pos in Positions.Where(p => p.Label == Label && p.SymbolCode == Symbol.Code && p.TradeType == TradeType.Buy))
            {
                double? li_16 = Math.Round(ai_4 + ad_8 * Symbol.PipSize, Symbol.Digits);
                if (pos.TakeProfit != li_16)
                    ModifyPosition(pos, pos.StopLoss, li_16);
            }
        }

        private void f0_88(double ai_4, int ad_8)
        {
            foreach (var pos in Positions.Where(p => p.Label == Label && p.SymbolCode == Symbol.Code && p.TradeType == TradeType.Sell))
            {
                double? li_16 = Math.Round(ai_4 - ad_8 * Symbol.PipSize, Symbol.Digits);
                if (pos.TakeProfit != li_16)
                    ModifyPosition(pos, pos.StopLoss, li_16);
            }
        }

        private void RCN()
        {
            if (o_tm(TradeType.Buy) > 1)
            {
                double y = pnt_12(TradeType.Buy);
                ChartObjects.DrawHorizontalLine("bpoint", y, Colors.Yellow, 2, LineStyle.Dots);
            }
            else
                ChartObjects.RemoveObject("bpoint");

            if (o_tm(TradeType.Sell) > 1)
            {
                double z = pnt_12(TradeType.Sell);
                ChartObjects.DrawHorizontalLine("spoint", z, Colors.HotPink, 2, LineStyle.Dots);
            }
            else
                ChartObjects.RemoveObject("spoint");

            ChartObjects.DrawText("pan", A_cmt_calc(), StaticPosition.TopLeft, Colors.Tomato);
        }

        private string A_cmt_calc()
        {
            string gc_78 = "";
            string wn_7 = "";
            string wn_8 = "";
            string sp_4 = "";
            string ppb = "";
            string lpb = "";
            string nb_6 = "";

            int buyPosCount = o_tm(TradeType.Buy);
            int sellPosCount = o_tm(TradeType.Sell);

            sp_4 = "\nSpread = " + Math.Round(sp_d, 1);
            nb_6 = "\n";

            if (buyPosCount > 0)
                wn_7 = "\nBuy Positions = " + buyPosCount;
            if (sellPosCount > 0)
                wn_8 = "\nSell Positions = " + sellPosCount;

            if (buyPosCount > 0)
            {
                double igl = Math.Round((pnt_12(TradeType.Buy) - Symbol.Bid) / Symbol.PipSize, 1);
                ppb = "\nBuy Target Away = " + igl;
            }
            if (sellPosCount > 0)
            {
                double osl = Math.Round((Symbol.Ask - pnt_12(TradeType.Sell)) / Symbol.PipSize, 1);
                lpb = "\nSell Target Away = " + osl;
            }

            if (EnableTrendFilter)
            {
                double maValue = _trendMA.Result.LastValue;
                string trendStatus = "RANGING";
                if (Symbol.Ask > maValue) trendStatus = "UPTREND";
                else if (Symbol.Bid < maValue) trendStatus = "DOWNTREND";
                nb_6 += "\nTrend (MA " + TrendMAPeriod + ") = " + trendStatus;
            }


            if (sp_d > MaxSpread)
                gc_78 = "MAX SPREAD EXCEED";
            else
                gc_78 = "Smart Grid" + nb_6 + wn_7 + sp_4 + wn_8 + ppb + lpb;
            return (gc_78);
        }

        private int cnt_16()
        {
            int ASide = 0;
            for (int i = Positions.Count - 1; i >= 0; i--)
            {
                position = Positions[i];
                if (position.Label == Label && position.SymbolCode == Symbol.Code)
                    ASide++;
            }
            return ASide;
        }

        private int o_tm(TradeType TrdTp)
        {
            int TSide = 0;
            for (int i = Positions.Count - 1; i >= 0; i--)
            {
                position = Positions[i];
                if (position.Label == Label && position.SymbolCode == Symbol.Code)
                {
                    if (position.TradeType == TrdTp)
                        TSide++;
                }
            }
            return TSide;
        }

        private double pnt_12(TradeType TrdTp)
        {
            double Result = 0;
            double AveragePrice = 0;
            long Count = 0;

            for (int i = Positions.Count - 1; i >= 0; i--)
            {
                position = Positions[i];
                if (position.Label == Label && position.SymbolCode == Symbol.Code)
                {
                    if (position.TradeType == TrdTp)
                    {
                        AveragePrice += position.EntryPrice * position.Volume;
                        Count += position.Volume;
                    }
                }
            }
            if (AveragePrice > 0 && Count > 0)
                Result = Math.Round(AveragePrice / Count, Symbol.Digits);
            return Result;
        }

        private double D_TD(TradeType TrdTp)
        {
            double D_TD = 0;
            for (int i = Positions.Count - 1; i >= 0; i--)
            {
                position = Positions[i];
                if (position.Label == Label && position.SymbolCode == Symbol.Code)
                {
                    if (position.TradeType == TrdTp)
                    {
                        if (D_TD == 0)
                        {
                            D_TD = position.EntryPrice;
                            continue;
                        }
                        if (position.EntryPrice < D_TD)
                            D_TD = position.EntryPrice;
                    }
                }
            }
            return D_TD;
        }

        private double U_TD(TradeType TrdTp)
        {
            double U_TD = 0;
            for (int i = Positions.Count - 1; i >= 0; i--)
            {
                position = Positions[i];
                if (position.Label == Label && position.SymbolCode == Symbol.Code)
                {
                    if (position.TradeType == TrdTp)
                    {
                        if (U_TD == 0)
                        {
                            U_TD = position.EntryPrice;
                            continue;
                        }
                        if (position.EntryPrice > U_TD)
                            U_TD = position.EntryPrice;
                    }
                }
            }
            return U_TD;
        }

        private double f_tk(TradeType TrdTp)
        {
            double prc_4 = 0;
            int tk_4 = 0;
            for (int i = Positions.Count - 1; i >= 0; i--)
            {
                position = Positions[i];
                if (position.Label == Label && position.SymbolCode == Symbol.Code)
                {
                    if (position.TradeType == TrdTp)
                    {
                        if (tk_4 == 0 || tk_4 > position.Id)
                        {
                            prc_4 = position.EntryPrice;
                            tk_4 = position.Id;
                        }
                    }
                }
            }
            return prc_4;
        }

        private long lt_8(TradeType TrdTp)
        {
            long lot_4 = 0;
            int tk_4 = 0;
            for (int i = Positions.Count - 1; i >= 0; i--)
            {
                position = Positions[i];
                if (position.Label == Label && position.SymbolCode == Symbol.Code)
                {
                    if (position.TradeType == TrdTp)
                    {
                        if (tk_4 == 0 || tk_4 > position.Id)
                        {
                            lot_4 = position.Volume;
                            tk_4 = position.Id;
                        }
                    }
                }
            }
            return lot_4;
        }

        private long clt(TradeType TrdTp)
        {
            long Result = 0;
            for (int i = Positions.Count - 1; i >= 0; i--)
            {
                position = Positions[i];
                if (position.Label == Label && position.SymbolCode == Symbol.Code)
                {
                    if (position.TradeType == TrdTp)
                        Result += position.Volume;
                }
            }
            return Result;
        }

        private int Grd_Ex(TradeType ai_0, TradeType ci_0)
        {
            double prc_4 = f_tk(ci_0);
            int tk_4 = 0;
            for (int i = Positions.Count - 1; i >= 0; i--)
            {
                position = Positions[i];
                if (position.Label == Label && position.SymbolCode == Symbol.Code)
                {
                    if (position.TradeType == ai_0 && ai_0 == TradeType.Buy)
                    {
                        if (Math.Round(position.EntryPrice, Symbol.Digits) <= Math.Round(prc_4, Symbol.Digits))
                            tk_4++;
                    }
                    if (position.TradeType == ai_0 && ai_0 == TradeType.Sell)
                    {
                        if (Math.Round(position.EntryPrice, Symbol.Digits) >= Math.Round(prc_4, Symbol.Digits))
                            tk_4++;
                    }
                }
            }
            return (tk_4);
        }

        private long n_lt(TradeType ca_8)
        {
            int ic_g = Grd_Ex(ca_8, ca_8);
            long gi_c = lt_8(ca_8);
            long ld_4 = Symbol.NormalizeVolume(gi_c * Math.Pow(VolumeExponent, ic_g));
            return (ld_4);
        }

        private long fer(long ic_9, int bk_4)
        {
            long ga_i = Symbol.VolumeMin;
            long gd_i = Symbol.VolumeStep;
            long dc_i = Symbol.VolumeMax;
            long ic_8 = ic_9;
            if (ic_8 < ga_i)
                ic_8 = ga_i;
            if (ic_8 > dc_i)
                ic_8 = dc_i;
            return (ic_8);
        }

        private void CloseAllBotPositions()
        {
            Print("Attempting to close all positions managed by this bot...");
            foreach (var pos in Positions.Where(p => p.Label == Label && p.SymbolCode == Symbol.Code).ToList())
            {
                try
                {
                    ClosePosition(pos);
                    Print($"Closed position ID: {pos.Id}, Type: {pos.TradeType}, Volume: {pos.Volume}");
                }
                catch (Exception e)
                {
                    Print($"Error closing position ID {pos.Id}: {e.Message}");
                }
            }
            Print("Finished attempting to close all bot positions.");
        }

        // --- NEW METHOD TO CALCULATE AND SAVE DAILY PROFIT ---
        private void CalculateAndSaveDailyProfit(DateTime recordDate)
        {
            if (!EnableDailyProfitLog)
            {
                Print("Daily Profit CSV Log is disabled.");
                return;
            }

            double currentBalance = Account.Balance;
            double dailyProfit = currentBalance - _previousBalance;

            // Handle the case where the bot just started or was reset and previousBalance is current.
            // Only record if there's an actual change in balance or if it's the start of a new day.
            if (dailyProfit == 0 && recordDate.Date == _lastDailyRecordDate.Date)
            {
                Print($"No profit change for {recordDate.ToString("yyyy-MM-dd")}. Skipping record.");
                return;
            }

            string dateString = recordDate.ToString("yyyy-MM-dd");
            // *** CHANGED: Use semicolon as separator for data line ***
            string dataLine = $"{dateString};{dailyProfit:F2}"; // Format to 2 decimal places

            try
            {
                // Append new data line to the file
                File.AppendAllText(DailyProfitCsvPath, dataLine + Environment.NewLine);
                Print($"Recorded daily profit for {dateString}: {dailyProfit:F2}. File updated at {DailyProfitCsvPath}");

                // Update previous balance for the next day's calculation
                _previousBalance = currentBalance;
                // Use InstanceId for LastBalance to ensure unique storage per bot instance
                LocalStorage.SetString($"LastBalance_{Symbol.Code}_{InstanceId}", _previousBalance.ToString());
            }
            catch (Exception ex)
            {
                Print($"ERROR: Could not write daily profit to file {DailyProfitCsvPath}. Exception: {ex.Message}");
            }
        }
        // --- END NEW METHOD ---
    }
}
