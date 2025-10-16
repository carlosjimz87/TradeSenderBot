# TradeSenderBot + Telegram — Quick Start (NinjaTrader)

This short guide walks you through:

1) Installing the **TradeSenderBot** Ninjatrader script on a chart  
2) Creating & wiring a **Telegram bot** so your trades get posted to chat  
3) Testing & troubleshooting

---

## 0) Requirements

- **NinjaTrader 8** (tested with 8.x)  
- A futures/forex/CFD instrument with historical data (for candles context)  
- Optional: A running **API** (if you want charts & storage)  
- A **Telegram** account (mobile or desktop) to create a bot

---

## 1) Import & place the indicator on a chart

1. Open NinjaTrader → **New** → **NinjaScript Editor**  
2. Right‑click the `Indicators` folder → **New Indicator** → close the dialog.  
3. Replace the new file content with the provided **`TradeSenderBot`** full script and **Compile** (F5).  
   - You should see `TradeSenderBot [version code]` in the Output window when it initializes.
4. Open a chart for your instrument (e.g., **MNQ SEP25**).  
5. Right‑click chart → **Indicators…** → add **TradeSenderBot**.

### Recommended properties

- **EnabledPosting**: `True` *(turn this on only after TelegramToken or ApiUrl are set)*  
- **Environment**: leave **blank** to auto (backtest/sim/real inferred from account name) or set explicitly to `backtest|sim|real`  
- **ApiUrl**: [Optional] your API endpoint (e.g., `http://YOUR-HOST:5055/upload`) to track trades besides the Telegram bot.
- **AccountName**: Choose the account you execute on 
- **ContextBars**: [Optional] The number `30` *(bars either side of entry/exit for chart)*  to take in context when sending to the API to be able to draw the chart.
- **IncludeExitContext**: `True` — whether to include a small candle window around the **exit point** in addition to the **entry** (makes the generated trade chart show the full trade from entry to exit)
- **CommissionPerContract**: `1.20` *(round-trip per contract)*  
- **DetectTP_SL**: `True` *(reads ATM “Target/Stop” orders to include TP/SL)*  
- **TelegramEnabled**: `True` if you want chat notifications  
- **TelegramBotToken**: Token of your Telegram bot (obtained from BotFather)
- **TelegramChatId**: Numeric chat ID (user or group) where you have your bot as admin and want to post the trades.

> If **ApiUrl** is empty, posting is automatically disabled for safety.  
> Telegram also remains disabled unless both Token & ChatId are provided.

When the indicator starts you’ll see a checklist in **New → Output** (or Control Center → **Log**).

---

## 2) Create a Telegram bot

1. In Telegram, open **@BotFather** and send `/newbot`.  
2. Give it a **name** and a **username** (must end in `bot`, e.g., `TradeRecorderBot`).  
3. Copy your **HTTP API token** (looks like `123456:ABC-...`).  
4. Start a chat with your bot (maybe you need to make it admin if its a group) (click the link BotFather provides) and send **/start** once so it can message you.

### Find your chat ID

- Easiest (personal chat): open a browser to  
  `https://api.telegram.org/bot<YOUR_TOKEN>/getUpdates`  
  Send a message to the bot and refresh. In the JSON you’ll see `"chat":{"id": ... }` → that’s your **chat_id**.
- For groups: add the bot to the group, send a message, and inspect `getUpdates` in the same way.

### Enter credentials in the indicator

On the chart, open **Indicators… → TradeSenderBot** and set:

- **TelegramEnabled**: `True`  
- **TelegramBotToken**: *(paste token)*  
- **TelegramChatId**: *(paste numeric id)*

Compile is not required—just click **OK**.

---

## 3) Send a test trade

- Place a quick **market entry** and **market exit** (or flatten a position).  
- The indicator buffers fills until your **net position returns to 0**, then it computes the trade and posts:
  - JSON to `ApiUrl` (if EnabledPosting = True and ApiUrl set)
  - A Telegram message like:
  - 
![Telegram Example](./tradeshot.png)

If you connected the API, your server can respond with a chart preview and save it for later.

---

## 4) Message format in Telegram

```
[BACKTEST] 25-Aug-2025 16:04 MNQ SEP25 Buy @23494.5 TP:23517.75 SL:23478.25 Qty:1 LOSS -16.25 pts $33.72
```

- **Environment** in brackets (BACKTEST|SIM|REAL)  
- **Time** in your local timezone  
- **Symbol**, **side**, **entry**, **TP/SL** (if detected), **Qty**  
- Outcome as **WON/LOSS/FLAT**, **points** and **net USD** (commission included)

> TP/SL are inferred from ATM order names containing `Target/Profit` or `Stop`. If you don’t use ATM or names differ, TP/SL may be `—`.

---

## 5) Troubleshooting

- **No messages appear**  
  - Check **EnabledPosting** and **ApiUrl**; server must be reachable from NT machine.  
  - See Control Center **Log** / Output for `[HTTP]` lines and HTTP status codes.
- **Telegram sends nothing**  
  - Ensure **TelegramEnabled=True**, token & chat_id set; look for `[Telegram]` lines in logs.  
- **Wrong environment**  
  - Leave **Environment** blank to auto‑derive (`Playback` → backtest, `Sim` → sim, otherwise real), or set it explicitly.
- **Duplicate/partial trades**  
  - Indicator only posts **after flat** (net=0). If you scale in/out multiple times before flat, it aggregates fills into one entry/exit pair using average prices.

---

## 6) Safety & privacy

- The script **never logs** your Telegram token or chat ID.  
- **EnabledPosting** defaults to **False** and **ApiUrl** blank on release—no data leaves your PC unless you opt‑in.  
- If you distribute this indicator, keep these defaults as-is and let users bring their own API & bot credentials.

---

## 7) Quick reference (properties)

- `EnabledPosting` *(bool)* – master switch for HTTP posting  
- `Environment` *(string)* – `backtest|sim|real` or leave blank to auto  
- `ApiUrl` *(string)* – e.g., `http://HOST:5055/upload`  
- `AccountName` *(string)* – used in ID & logs (default `Sim101`)  
- `ContextBars` *(int)* – bars of context before/after entry/exit (default 30)  
- `IncludeExitContext` *(bool)* – include a window around exit  
- `CommissionPerContract` *(double)* – per contract round-trip (net calc)  
- `DetectTP_SL` *(bool)* – detect from order names (Target/Stop)  
- `TelegramEnabled` *(bool)* – send Telegram message if true  
- `TelegramBotToken` *(string)* – BotFather token  
- `TelegramChatId` *(string)* – destination chat/group id

Happy trading! ✨

[🇪🇸 Leer en Español](./README.es.md)