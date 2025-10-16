#region Using
using System;
using System.Text;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using System.Net.Http;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using System.Security.Cryptography;

using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class AccountNameConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            try
            {
                var names = Account.All
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Name))
                    .Select(a => a.Name)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
                if (names.Count == 0) names.Add("Sim101");
                return new StandardValuesCollection(names);
            }
            catch { return new StandardValuesCollection(new List<string> { "Sim101" }); }
        }
    }

    public class ManualTradePoster : Indicator
    {
        private const string VERSION = "v1.0.0";

        // ===== Inputs =====
        [NinjaScriptProperty, Display(Name="EnabledPosting", GroupName="Upload", Order=0)]
        public bool EnabledPosting { get; set; } = true;

        [NinjaScriptProperty, Display(Name="Environment", Description="backtest | sim | real", GroupName="Upload", Order=1)]
        public string EnvironmentName { get; set; } = "backtest";

        [NinjaScriptProperty, Display(Name="ApiUrl", Description="http://HOST:5055/upload", GroupName="Upload", Order=2)]
        public string ApiUrl { get; set; } = "";

        [NinjaScriptProperty, Display(Name="AccountName", GroupName="Upload", Order=3)]
        [TypeConverter(typeof(AccountNameConverter))]
        public string AccountName { get; set; } = "Playback101";

        [NinjaScriptProperty, Display(Name="ContextBars", GroupName="Data", Order=4)]
        public int ContextBars { get; set; } = 30;

        [NinjaScriptProperty, Display(Name="IncludeExitContext", GroupName="Data", Order=5)]
        public bool IncludeExitContext { get; set; } = true;

        [NinjaScriptProperty, Display(Name="CommissionPerContract", GroupName="PNL", Order=6)]
        public double CommissionPerContract { get; set; } = 1.72;

        [NinjaScriptProperty, Display(Name="DetectTP_SL", GroupName="PNL", Order=7)]
        public bool DetectTPSL { get; set; } = true;

        [NinjaScriptProperty, Display(Name="TelegramEnabled", GroupName="Telegram", Order=8)]
        public bool TelegramEnabled { get; set; } = true;

        [NinjaScriptProperty, Display(Name="TelegramBotToken", GroupName="Telegram", Order=9)]
        public string TelegramBotToken { get; set; } = "";

        [NinjaScriptProperty, Display(Name="TelegramChatId", GroupName="Telegram", Order=10)]
        public string TelegramChatId { get; set; } = "";

        // ===== State =====
        private Account acct;
        private int netQty = 0, prevNetQty = 0;
        private readonly List<Execution> bufferedExecs = new List<Execution>();
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private double? curTP = null, curSL = null;
        private bool currLong = true;
        private int tradeQty = 0;

        private class TradePostPayload
        {
            public string TradeId;
            public string Symbol;
            public double Entry;
            public double Exit;
            public DateTime EntryTime;
            public DateTime ExitTime;
            public double? TP;
            public double? SL;
            public int ExitBar;
            public bool IsLong;
            public int Qty;
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ManualTradePoster";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                IsSuspendedWhileInactive = false;
            }
            else if (State == State.DataLoaded)
            {
                EnvironmentName = (EnvironmentName ?? "sim").Trim().ToLower();
                if (EnvironmentName != "backtest" && EnvironmentName != "sim" && EnvironmentName != "real")
                    EnvironmentName = "sim";
                if (ContextBars < 0) ContextBars = 0;

                Print($"[Init {VERSION}] {Instrument.FullName} Env={EnvironmentName} ApiUrl={ApiUrl} Acct={AccountName} Ctx={ContextBars} IncludeExit={IncludeExitContext}");

                try { acct = Account.All.FirstOrDefault(a => string.Equals(a.Name, AccountName, StringComparison.OrdinalIgnoreCase)); }
                catch { acct = null; }

                if (acct != null)
                {
                    acct.ExecutionUpdate += OnAccountExecution;
                    acct.OrderUpdate += OnAccountOrder;
                    Print($"[Init] Subscribed to '{acct.Name}'.");
                }
                else
                    Print($"[WARN] Account '{AccountName}' not found.");
            }
            else if (State == State.Terminated)
            {
                if (acct != null)
                {
                    acct.ExecutionUpdate -= OnAccountExecution;
                    acct.OrderUpdate -= OnAccountOrder;
                    Print("[Shutdown] Unsubscribed.");
                }
            }
        }

        protected override void OnBarUpdate() { }

        // ---------- TP/SL heuristic (name + type/side) ----------
        private void OnAccountOrder(object sender, OrderEventArgs e)
        {
            if (!EnabledPosting || !DetectTPSL) return;

            var o = e?.Order;
            if (o == null || o.Instrument == null || netQty == 0) return;
            if (!string.Equals(o.Instrument.FullName, Instrument.FullName, StringComparison.OrdinalIgnoreCase)) return;

            bool isSell = o.OrderAction == OrderAction.Sell || o.OrderAction == OrderAction.SellShort;
            bool isBuy  = o.OrderAction == OrderAction.Buy  || o.OrderAction == OrderAction.BuyToCover;

            // Exit side only
            if (currLong && !isSell) return;
            if (!currLong && !isBuy) return;

            string name = (o.Name ?? "").ToLowerInvariant();

            // 1) Name hints
            if (name.Contains("target") || name.Contains("profit"))
            {
                double? val = null;
                if (o.LimitPrice > 0) val = o.LimitPrice;
                else if (o.StopPrice > 0) val = o.StopPrice;
                if (val.HasValue) { curTP = val; Print($"[TP/SL] TP inferred (name): {curTP}"); }
            }
            else if (name.Contains("stop"))
            {
                double? val = null;
                if (o.StopPrice > 0) val = o.StopPrice;
                else if (o.LimitPrice > 0) val = o.LimitPrice;
                if (val.HasValue) { curSL = val; Print($"[TP/SL] SL inferred (name): {curSL}"); }
            }
            else
            {
                // 2) Fallback by type
                if (o.LimitPrice > 0)
                {
                    curTP = o.LimitPrice;
                    Print($"[TP/SL] TP inferred (type): {curTP}");
                }
                if (o.StopPrice > 0)
                {
                    curSL = o.StopPrice;
                    Print($"[TP/SL] SL inferred (type): {curSL}");
                }
            }
        }

        // Scan working exit-side orders; used after entry and right before posting
        private void ScanBracketCandidates(bool isLong)
        {
            try
            {
                if (acct == null) return;

                foreach (var o in acct.Orders)
                {
                    if (o == null || o.Instrument == null) continue;
                    if (!string.Equals(o.Instrument.FullName, Instrument.FullName, StringComparison.OrdinalIgnoreCase)) continue;

                    bool isSell = o.OrderAction == OrderAction.Sell || o.OrderAction == OrderAction.SellShort;
                    bool isBuy  = o.OrderAction == OrderAction.Buy  || o.OrderAction == OrderAction.BuyToCover;
                    bool exitSide = (isLong && isSell) || (!isLong && isBuy);
                    if (!exitSide) continue;

                    if (o.LimitPrice > 0 && !curTP.HasValue) curTP = o.LimitPrice;
                    if (o.StopPrice  > 0 && !curSL.HasValue) curSL = o.StopPrice;
                }

                if (curTP.HasValue) Print($"[TP/SL] TP scan → {curTP}");
                if (curSL.HasValue) Print($"[TP/SL] SL scan → {curSL}");
            }
            catch (Exception ex)
            {
                Print("[TP/SL] Scan error: " + ex.Message);
            }
        }

        private void OnAccountExecution(object sender, ExecutionEventArgs e)
        {
            try
            {
                if (!EnabledPosting) return;
                var ex = e?.Execution;
                if (ex?.Instrument == null) return;
                if (!string.Equals(ex.Instrument.FullName, Instrument.FullName, StringComparison.OrdinalIgnoreCase))
                    return;

                Print($"[AcctExec] {ex.Instrument.FullName} {ex.Order?.OrderAction} Price={ex.Price} Qty={ex.Quantity} Time={ex.Time:o}");

                prevNetQty = netQty;
                int q = Math.Abs(ex.Quantity);
                switch (ex.Order?.OrderAction)
                {
                    case OrderAction.Buy:
                    case OrderAction.BuyToCover: netQty += q; break;
                    case OrderAction.Sell:
                    case OrderAction.SellShort:  netQty -= q; break;
                }
                Print($"[AcctPosCalc] Net qty: {prevNetQty} → {netQty}");

                if (prevNetQty == 0 && netQty != 0)
                {
                    bufferedExecs.Clear();
                    curTP = null; curSL = null;
                    tradeQty = 0;
                    Print("[Detect] Position opened → start buffering fills.");

                    if (bufferedExecs.Count == 0 && ex?.Order != null)
                        currLong = (ex.Order.OrderAction == OrderAction.Buy || ex.Order.OrderAction == OrderAction.BuyToCover);

                    // grab early bracket orders if already working
                    ScanBracketCandidates(currLong);
                }

                if (netQty != 0)
                {
                    bufferedExecs.Add(ex);
                    if (bufferedExecs.Count == 1 && ex.Order != null)
                        currLong = (ex.Order.OrderAction == OrderAction.Buy || ex.Order.OrderAction == OrderAction.BuyToCover);

                    if (ex.Order != null)
                    {
                        bool isEntrySide = currLong
                            ? (ex.Order.OrderAction == OrderAction.Buy || ex.Order.OrderAction == OrderAction.BuyToCover)
                            : (ex.Order.OrderAction == OrderAction.Sell || ex.Order.OrderAction == OrderAction.SellShort);
                        if (isEntrySide) tradeQty += Math.Abs(ex.Quantity);
                    }
                    Print($"[Detect] Buffered fills: {bufferedExecs.Count}, tradeQty={tradeQty}, currLong={currLong}");
                }

                if (prevNetQty != 0 && netQty == 0)
                {
                    bufferedExecs.Add(ex);
                    Print($"[Detect] Position flat. Total buffered fills: {bufferedExecs.Count}. Computing…");

                    if (TryComputeTrade(bufferedExecs, out double entryAvg, out double exitAvg, out DateTime entryTime, out DateTime exitTime))
                    {
                        int exitBar = Bars.GetBar(exitTime); if (exitBar < 0) exitBar = CurrentBar;

                        string tradeId = MakeTradeId(AccountName, Instrument.FullName, entryTime, exitTime, entryAvg, exitAvg, Math.Max(1, tradeQty), currLong);

                        var payload = new TradePostPayload
                        {
                            TradeId = tradeId,
                            Symbol = Instrument.FullName,
                            Entry = entryAvg,
                            Exit = exitAvg,
                            EntryTime = entryTime,
                            ExitTime = exitTime,
                            TP = curTP,
                            SL = curSL,
                            ExitBar = exitBar,
                            IsLong = currLong,
                            Qty = Math.Max(1, tradeQty)
                        };
                        TriggerCustomEvent(o => SendTrade((TradePostPayload)o), payload);
                    }
                    else Print("[WARN] Could not compute trade from buffered executions.");

                    bufferedExecs.Clear();
                }
            }
            catch (Exception ex2)
            {
                Print("[ERROR] OnAccountExecution: " + ex2.Message);
            }
        }

        private bool TryComputeTrade(List<Execution> fills, out double entryAvg, out double exitAvg, out DateTime entryTime, out DateTime exitTime)
        {
            entryAvg = exitAvg = 0;
            entryTime = exitTime = DateTime.MinValue;
            if (fills == null || fills.Count == 0) return false;

            double buyNotional = 0; int buyQty = 0; DateTime? firstBuy = null, lastBuy = null;
            double sellNotional = 0; int sellQty = 0; DateTime? firstSell = null, lastSell = null;

            foreach (var ex in fills)
            {
                if (ex == null || ex.Quantity == 0 || ex.Order == null) continue;
                int q = Math.Abs(ex.Quantity);

                if (ex.Order.OrderAction == OrderAction.Buy || ex.Order.OrderAction == OrderAction.BuyToCover)
                {
                    buyNotional += ex.Price * q; buyQty += q;
                    if (!firstBuy.HasValue) firstBuy = ex.Time; lastBuy = ex.Time;
                }
                else if (ex.Order.OrderAction == OrderAction.Sell || ex.Order.OrderAction == OrderAction.SellShort)
                {
                    sellNotional += ex.Price * q; sellQty += q;
                    if (!firstSell.HasValue) firstSell = ex.Time; lastSell = ex.Time;
                }
            }

            if (buyQty == 0 && sellQty == 0) return false;

            var first = fills[0];
            bool longTrade = first.Order != null &&
                (first.Order.OrderAction == OrderAction.Buy || first.Order.OrderAction == OrderAction.BuyToCover);

            if (longTrade)
            {
                if (buyQty == 0 || sellQty == 0) return false;
                entryAvg = buyNotional / buyQty;
                exitAvg  = sellNotional / sellQty;
                entryTime = firstBuy ?? first.Time;
                exitTime  = lastSell ?? first.Time;
            }
            else
            {
                if (sellQty == 0 || buyQty == 0) return false;
                entryAvg = sellNotional / sellQty;
                exitAvg  = buyNotional / buyQty;
                entryTime = firstSell ?? first.Time;
                exitTime  = lastBuy ?? first.Time;
            }
            return true;
        }

        private void SendTrade(TradePostPayload p)
        {
            try
            {
                // Last-chance scan in case bracket orders were created/renamed late
                ScanBracketCandidates(p.IsLong);

                string env = NormalizeEnvironment(EnvironmentName, AccountName);
                double tickSize = Instrument.MasterInstrument.TickSize;
                double pointValue = Instrument.MasterInstrument.PointValue;
                double tickValue = pointValue * tickSize;

                // candles (entry/exit windows)
                int eBar = Bars.GetBar(p.EntryTime); if (eBar < 0) eBar = CurrentBar;
                int xBar = p.ExitBar < 0 ? CurrentBar : p.ExitBar;

                string candlesEntry = BuildCandlesJsonSafe(eBar - ContextBars, eBar + ContextBars);
                string candlesExit  = IncludeExitContext ? BuildCandlesJsonSafe(xBar - ContextBars, xBar + ContextBars) : "[]";

                // meta (includes environment)
                string metaJson = "{"
                    + "\"trade_id\":\"" + Escape(p.TradeId) + "\","
                    + "\"environment\":\"" + Escape(env) + "\","
                    + "\"accountName\":\"" + Escape(AccountName ?? "") + "\","
                    + "\"symbol\":\"" + Escape(p.Symbol) + "\","
                    + "\"direction\":\"" + (p.IsLong ? "long" : "short") + "\","
                    + "\"qty\":" + p.Qty + ","
                    + "\"entryPrice\":" + p.Entry.ToString("0.####", CI) + ","
                    + "\"exitPrice\":"  + p.Exit.ToString("0.####", CI) + ","
                    + "\"takeProfit\":" + (p.TP.HasValue ? p.TP.Value.ToString("0.####", CI) : "null") + ","
                    + "\"stopLoss\":"   + (p.SL.HasValue ? p.SL.Value.ToString("0.####", CI) : "null") + ","
                    + "\"tickSize\":"   + tickSize.ToString("0.####", CI) + ","
                    + "\"tickValue\":"  + tickValue.ToString("0.####", CI) + ","
                    + "\"commission\":" + CommissionPerContract.ToString("0.####", CI) + ","
                    + "\"entryTime\":\""+ p.EntryTime.ToString("o") + "\","
                    + "\"exitTime\":\"" + p.ExitTime.ToString("o") + "\""
                    + "}";

                // single JSON envelope for /upload
                string body = "{"
                    + "\"meta\":" + metaJson + ","
                    + "\"candles_entry\":" + (candlesEntry ?? "[]") + ","
                    + "\"candles_exit\":"  + (candlesExit ?? "[]")
                    + "}";

                Print($"[HTTP {VERSION}] POST {ApiUrl}");
                _ = PostJsonAndTelegramAsync(body, p, pointValue, env);
            }
            catch (Exception ex)
            {
                Print("[ERROR] SendTrade: " + ex.Message);
            }
        }

        private async Task PostJsonAndTelegramAsync(string body, TradePostPayload p, double pointValue, string env)
        {
            try
            {
                using (var http = new HttpClient())
                {
                    var res = await http.PostAsync(ApiUrl, new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
                    Print($"[HTTP] /upload: {(int)res.StatusCode} {res.ReasonPhrase}");

                    if (TelegramEnabled && !string.IsNullOrWhiteSpace(TelegramBotToken) && !string.IsNullOrWhiteSpace(TelegramChatId))
                    {
                        string caption = BuildCaption(env, p.ExitTime, p.Symbol, p.IsLong, p.Entry, p.Exit, p.TP, p.SL, p.Qty, pointValue, CommissionPerContract);
                        await SendTelegramCaptionOnly(http, "https://api.telegram.org/bot" + TelegramBotToken, caption).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Print("[ERROR] PostJsonAndTelegramAsync: " + ex.Message);
            }
        }

        private async Task SendTelegramCaptionOnly(HttpClient http, string tgRoot, string caption)
        {
            try
            {
                using (var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string,string>("chat_id", TelegramChatId),
                    new KeyValuePair<string,string>("text", caption),
                    new KeyValuePair<string,string>("disable_web_page_preview", "true")
                }))
                {
                    var resp = await http.PostAsync(tgRoot + "/sendMessage", form).ConfigureAwait(false);
                    Print($"[Telegram] sendMessage: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                Print("[Telegram] sendMessage error: " + ex.Message);
            }
        }

        // ---------- Helpers ----------
        private string BuildCaption(string env, DateTime exitTime, string symbol, bool isLong, double entry, double exit, double? tp, double? sl, int qty, double pointValue, double commissionPerContract)
        {
            string when = exitTime.ToLocalTime().ToString("dd-MMM-yyyy HH:mm", CI);
            string side = isLong ? "Buy" : "Sell";
            double ptsPer = isLong ? (exit - entry) : (entry - exit);
            double ptsTotal = ptsPer * Math.Max(1, qty);
            double grossUsd = ptsTotal * pointValue;
            double netUsd = grossUsd - (commissionPerContract * Math.Max(1, qty));
            string outcome = netUsd > 0 ? "WON" : (netUsd < 0 ? "LOSS" : "FLAT");
            string sPts = (ptsTotal >= 0 ? "+" : "") + ptsTotal.ToString("0.####", CI) + " pts";
            string sUsd = (netUsd >= 0 ? "+" : "") + "$" + Math.Abs(netUsd).ToString("0.00", CI);
            string tpStr = tp.HasValue ? tp.Value.ToString("0.####", CI) : "—";
            string slStr = sl.HasValue ? sl.Value.ToString("0.####", CI) : "—";
            string envTag = "[" + (env ?? "sim").ToUpperInvariant() + "]";
            return $"{envTag} {when} {symbol} {side} @{entry.ToString("0.####", CI)} TP:{tpStr} SL:{slStr} Qty:{qty} {outcome} {sPts} {sUsd}";
        }

        private static string NormalizeEnvironment(string envCandidate, string acctName)
        {
            string env = (envCandidate ?? "").Trim().ToLowerInvariant();
            if (env == "backtest" || env == "sim" || env == "real")
                return env;

            string a = (acctName ?? "").ToLowerInvariant();
            if (a.Contains("playback")) return "backtest";
            if (a.Contains("sim"))      return "sim";
            return "real";
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string MakeTradeId(string account, string symbol, DateTime entryTime, DateTime exitTime, double entry, double exit, int qty, bool isLong)
        {
            string raw = $"{account}|{symbol}|{entryTime:o}|{exitTime:o}|{entry:0.########}|{exit:0.########}|{qty}|{(isLong?"L":"S")}";
            using (var sha1 = SHA1.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(raw);
                var hash = sha1.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private int SafeBarsAgoFromAbs(int abs)
        {
            int ba = CurrentBar - abs;
            if (ba < 0 || ba > CurrentBar) return -1;
            return ba;
        }

        private string BuildCandlesJsonSafe(int fromAbs, int toAbs)
        {
            if (CurrentBar < 0) return "[]";
            int startAbs = Math.Max(0, fromAbs);
            int endAbs   = Math.Min(CurrentBar, toAbs);
            if (endAbs < startAbs) return "[]";

            var sb = new StringBuilder();
            sb.Append("[");
            bool first = true;

            for (int abs = startAbs; abs <= endAbs; abs++)
            {
                int barsAgo = SafeBarsAgoFromAbs(abs);
                if (barsAgo == -1) continue;

                string t = Time[barsAgo].ToString("o");
                string o = Open[barsAgo].ToString("0.####", CI);
                string h = High[barsAgo].ToString("0.####", CI);
                string l = Low[barsAgo].ToString("0.####", CI);
                string c = Close[barsAgo].ToString("0.####", CI);
                long v = 0; try { v = Convert.ToInt64(Volume[barsAgo]); } catch { }

                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"t\":\"").Append(t).Append("\",\"o\":").Append(o)
                  .Append(",\"h\":").Append(h).Append(",\"l\":").Append(l)
                  .Append(",\"c\":").Append(c).Append(",\"v\":").Append(v.ToString(CI)).Append("}");
            }

            sb.Append("]");
            return sb.ToString();
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private ManualTradePoster[] cacheManualTradePoster;
        public ManualTradePoster ManualTradePoster(bool enabledPosting, string environmentName, string apiUrl, string accountName, int contextBars, bool includeExitContext, double commissionPerContract, bool detectTPSL, bool telegramEnabled, string telegramBotToken, string telegramChatId)
        {
            return ManualTradePoster(Input, enabledPosting, environmentName, apiUrl, accountName, contextBars, includeExitContext, commissionPerContract, detectTPSL, telegramEnabled, telegramBotToken, telegramChatId);
        }

        public ManualTradePoster ManualTradePoster(ISeries<double> input, bool enabledPosting, string environmentName, string apiUrl, string accountName, int contextBars, bool includeExitContext, double commissionPerContract, bool detectTPSL, bool telegramEnabled, string telegramBotToken, string telegramChatId)
        {
            if (cacheManualTradePoster != null)
                for (int idx = 0; idx < cacheManualTradePoster.Length; idx++)
                    if (cacheManualTradePoster[idx] != null && cacheManualTradePoster[idx].EnabledPosting == enabledPosting && cacheManualTradePoster[idx].EnvironmentName == environmentName && cacheManualTradePoster[idx].ApiUrl == apiUrl && cacheManualTradePoster[idx].AccountName == accountName && cacheManualTradePoster[idx].ContextBars == contextBars && cacheManualTradePoster[idx].IncludeExitContext == includeExitContext && cacheManualTradePoster[idx].CommissionPerContract == commissionPerContract && cacheManualTradePoster[idx].DetectTPSL == detectTPSL && cacheManualTradePoster[idx].TelegramEnabled == telegramEnabled && cacheManualTradePoster[idx].TelegramBotToken == telegramBotToken && cacheManualTradePoster[idx].TelegramChatId == telegramChatId && cacheManualTradePoster[idx].EqualsInput(input))
                        return cacheManualTradePoster[idx];
            return CacheIndicator<ManualTradePoster>(new ManualTradePoster(){ EnabledPosting = enabledPosting, EnvironmentName = environmentName, ApiUrl = apiUrl, AccountName = accountName, ContextBars = contextBars, IncludeExitContext = includeExitContext, CommissionPerContract = commissionPerContract, DetectTPSL = detectTPSL, TelegramEnabled = telegramEnabled, TelegramBotToken = telegramBotToken, TelegramChatId = telegramChatId }, input, ref cacheManualTradePoster);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.ManualTradePoster ManualTradePoster(bool enabledPosting, string environmentName, string apiUrl, string accountName, int contextBars, bool includeExitContext, double commissionPerContract, bool detectTPSL, bool telegramEnabled, string telegramBotToken, string telegramChatId)
        {
            return indicator.ManualTradePoster(Input, enabledPosting, environmentName, apiUrl, accountName, contextBars, includeExitContext, commissionPerContract, detectTPSL, telegramEnabled, telegramBotToken, telegramChatId);
        }

        public Indicators.ManualTradePoster ManualTradePoster(ISeries<double> input , bool enabledPosting, string environmentName, string apiUrl, string accountName, int contextBars, bool includeExitContext, double commissionPerContract, bool detectTPSL, bool telegramEnabled, string telegramBotToken, string telegramChatId)
        {
            return indicator.ManualTradePoster(input, enabledPosting, environmentName, apiUrl, accountName, contextBars, includeExitContext, commissionPerContract, detectTPSL, telegramEnabled, telegramBotToken, telegramChatId);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.ManualTradePoster ManualTradePoster(bool enabledPosting, string environmentName, string apiUrl, string accountName, int contextBars, bool includeExitContext, double commissionPerContract, bool detectTPSL, bool telegramEnabled, string telegramBotToken, string telegramChatId)
        {
            return indicator.ManualTradePoster(Input, enabledPosting, environmentName, apiUrl, accountName, contextBars, includeExitContext, commissionPerContract, detectTPSL, telegramEnabled, telegramBotToken, telegramChatId);
        }

        public Indicators.ManualTradePoster ManualTradePoster(ISeries<double> input , bool enabledPosting, string environmentName, string apiUrl, string accountName, int contextBars, bool includeExitContext, double commissionPerContract, bool detectTPSL, bool telegramEnabled, string telegramBotToken, string telegramChatId)
        {
            return indicator.ManualTradePoster(input, enabledPosting, environmentName, apiUrl, accountName, contextBars, includeExitContext, commissionPerContract, detectTPSL, telegramEnabled, telegramBotToken, telegramChatId);
        }
    }
}
#endregion