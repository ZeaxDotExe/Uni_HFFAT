using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Indicators;
using System.IO; // Required for file operations
using System.Text; // Required for StringBuilder (though not strictly needed for this simple case, good practice)

namespace cAlgo.Robots
{
    // * IMPORTANT: Changed AccessRights to FullAccess to allow file writing *
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class MyBollingerBands : Robot
    {
        [Parameter("Source")]
        public DataSeries Source { get; set; }
        [Parameter("BandPeriods", DefaultValue = 14)]
        public int BandPeriod { get; set; }
        [Parameter("Std", DefaultValue = 1.8)]
        public double std { get; set; }
        [Parameter("MAType")]
        public MovingAverageType MAType { get; set; }
        [Parameter("Initial Volume Percent", DefaultValue = 1, MinValue = 0)]
        public double InitialVolumePercent { get; set; }
        [Parameter("Stop Loss", DefaultValue = 100)]
        public int StopLoss { get; set; }
        [Parameter("Take Profit", DefaultValue = 100)]
        public int TakeProfit { get; set; }
        [Parameter("RSI Period", DefaultValue = 14)]
        public int RsiPeriod { get; set; }
        [Parameter("RSI Overbought", DefaultValue = 70)]
        public int RsiOverbought { get; set; }
        [Parameter("RSI Oversold", DefaultValue = 30)]
        public int RsiOversold { get; set; }

        // --- NEW PARAMETERS FOR CSV LOGGING ---
        [Parameter("Enable Daily Profit CSV Log", DefaultValue = true)]
        public bool EnableDailyProfitLog { get; set; }

        [Parameter("Daily Profit CSV Path", DefaultValue = "C:\\cBots\\MyBollingerBands_DailyProfit.csv")]
        public string DailyProfitCsvPath { get; set; }
        // --- END NEW PARAMETERS ---

        private BollingerBands boll;
        private RelativeStrengthIndex rsi;

        private DateTime _lastDailyRecordDate; // To track when the last daily profit was recorded
        private double _previousBalance; // To calculate daily profit

        protected override void OnStart()
        {
            boll = Indicators.BollingerBands(Source, BandPeriod, std, MAType);
            rsi = Indicators.RelativeStrengthIndex(Source, RsiPeriod);

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
                        // Use semicolon as separator for header
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
                            // Split by semicolon for reading last date
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

        protected override void OnBar()
        {
            var volumne = Math.Floor(Account.Balance * 10 * InitialVolumePercent / 10000) * 10000;

            double lastClose2 = Bars.Last(2).Close;
            double lastClose1 = Bars.Last(1).Close;
            double rsiValue = rsi.Result.Last(1);

            // --- Daily Profit Logging Check on new Daily Bar ---
            if (EnableDailyProfitLog)
            {
                // Check if a new day has started since the last profit was recorded
                // and if the current bar represents the start of a new day after a full previous day.
                if (Server.Time.Date > _lastDailyRecordDate)
                {
                    // Calculate and save for the day that just ended (i.e., the previous full day)
                    CalculateAndSaveDailyProfit(Server.Time.Date.AddDays(-1));
                    _lastDailyRecordDate = Server.Time.Date; // Update the last recorded date to the current day
                }
            }
            // --- End Daily Profit Logging Check ---

            if (Server.Time.Hour == 21 && Server.Time.Minute == 59)
            {
                foreach (var position in Positions)
                {
                    if (position.SymbolName == Symbol.Name)
                        ClosePosition(position);
                }
                // After closing positions at end of day, record the profit for that day.
                // This will catch profits from positions closed at day end.
                if (EnableDailyProfitLog && Server.Time.Date == _lastDailyRecordDate) // Ensure we only record once per day close
                {
                     // If this is the actual last bar of the day before a new day officially starts,
                     // we should calculate profit for THIS day if we just closed positions.
                     // A more robust check might involve comparing with next bar's date, but this is a good start.
                     CalculateAndSaveDailyProfit(Server.Time.Date);
                     _lastDailyRecordDate = Server.Time.Date; // Ensure it's marked as recorded for today
                }
                return; // Exit OnBar as we've done our end-of-day operations
            }

            if (lastClose2 > boll.Top.Last(2) && rsiValue > RsiOverbought)
            {
                if (lastClose1 < boll.Top.Last(1))
                {
                    ExecuteMarketOrder(TradeType.Sell, Symbol.Name, volumne, "ndnghiaBollinger", StopLoss, TakeProfit);
                }
            }
            else if (lastClose2 < boll.Bottom.Last(2) && rsiValue < RsiOversold)
            {
                if (lastClose1 > boll.Bottom.Last(1))
                {
                    ExecuteMarketOrder(TradeType.Buy, Symbol.Name, volumne, "ndnghiaBollinger", StopLoss, TakeProfit);
                }
            }
        }

        protected override void OnTick()
        {
        }

        protected override void OnStop()
        {
            // --- Save final daily profit if stopping mid-day ---
            if (EnableDailyProfitLog)
            {
                // Only record if current day has not been recorded yet or if there's an actual balance change.
                // This ensures we capture profit from the current (possibly partial) day.
                // We compare to _lastDailyRecordDate which should be the date of the last full day recorded.
                if (Server.Time.Date > _lastDailyRecordDate || Account.Balance != _previousBalance)
                {
                    CalculateAndSaveDailyProfit(Server.Time.Date); // Record for the current (possibly partial) day
                }

                // Save current balance for next start (using InstanceId for uniqueness)
                LocalStorage.SetString($"LastBalance_{Symbol.Code}_{InstanceId}", Account.Balance.ToString());
                Print($"Final balance saved: {Account.Balance}");
            }
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

            // Prevent writing duplicate entries for the same date if no profit change
            // This is especially important for the 'OnStop' call for the current day.
            if (recordDate.Date == _lastDailyRecordDate.Date && dailyProfit == 0 && currentBalance == _previousBalance)
            {
                Print($"No profit change for {recordDate.ToString("yyyy-MM-dd")}. Skipping record.");
                return;
            }

            string dateString = recordDate.ToString("yyyy-MM-dd");
            // Use semicolon as separator for data line
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