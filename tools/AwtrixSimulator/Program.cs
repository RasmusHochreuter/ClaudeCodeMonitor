using System.Collections.Concurrent;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var apps = new ConcurrentDictionary<string, JsonElement>();

app.MapPost("/api/custom", async (HttpRequest request, string name) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(body) || body.Trim() is "{}" or "null")
    {
        apps.TryRemove(name, out _);
        return Results.Ok();
    }

    apps[name] = JsonDocument.Parse(body).RootElement.Clone();
    return Results.Ok();
});

app.MapPost("/api/settings", () => Results.Ok());

// The shown app lives server-side so the monitor's /api/switch and the page's ◀ ▶ agree.
string? currentApp = null;
string? ShownApp() => currentApp is { } shown && apps.ContainsKey(shown) ? shown : apps.Keys.Order().FirstOrDefault();
app.MapPost("/api/switch", (SwitchRequest request) =>
{
    if (!apps.ContainsKey(request.Name))
    {
        return Results.BadRequest();
    }

    currentApp = request.Name;
    return Results.Ok();
});
app.MapGet("/api/stats", () => Results.Json(new { app = ShownApp() }));

// Fake Anthropic usage endpoint for end-to-end testing: point the monitor at
// this host via Monitor:UsageBaseUrl, then adjust values with POST /fake-usage.
var fakeUsage = new FakeUsage();
var outage = false;
var pendingCelebration = false;
var live = true;
var pokeSeq = 0;
var pokeAllowsCelebration = false;
var anthropic = new HttpClient { BaseAddress = new Uri("https://api.anthropic.com/"), Timeout = TimeSpan.FromSeconds(15) };
app.MapGet("/poke", () => Results.Text($"{pokeSeq}:{(pokeAllowsCelebration ? 1 : 0)}"));
app.MapGet("/api/oauth/usage", async (HttpRequest request) =>
{
    if (live)
    {
        // LIVE scenario: proxy the monitor's request to the real Anthropic
        // endpoint, passing its own auth headers through untouched.
        using var forward = new HttpRequestMessage(HttpMethod.Get, "api/oauth/usage");
        foreach (var header in new[] { "Authorization", "anthropic-beta" })
        {
            if (request.Headers.TryGetValue(header, out var value))
            {
                forward.Headers.TryAddWithoutValidation(header, (string)value!);
            }
        }

        try
        {
            using var response = await anthropic.SendAsync(forward);
            var body = await response.Content.ReadAsStringAsync();

            // Adopt the real window timestamps as the fake ones, so switching
            // from LIVE to a scenario keeps the same resets_at and never
            // fabricates a window-reset pattern.
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("five_hour", out var fiveHour) &&
                    fiveHour.TryGetProperty("resets_at", out var sessionResets) &&
                    sessionResets.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(sessionResets.GetString(), out var sessionAt))
                {
                    fakeUsage.SessionResetsAt = sessionAt;
                }

                if (doc.RootElement.TryGetProperty("seven_day", out var sevenDay) &&
                    sevenDay.TryGetProperty("resets_at", out var weeklyResets) &&
                    weeklyResets.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(weeklyResets.GetString(), out var weeklyAt))
                {
                    fakeUsage.WeeklyResetsAt = weeklyAt;
                }
            }
            catch (JsonException)
            {
            }

            return Results.Text(body, "application/json", null, (int)response.StatusCode);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(502);
        }
    }

    if (outage)
    {
        // 401 puts the monitor into the issue state on the very next poll,
        // so the button works instantly instead of after three failed polls.
        return Results.StatusCode(401);
    }

    // resets_at must be stable between requests — a timestamp that creeps
    // forward on every poll would look like an endless stream of window
    // resets to the monitor and re-trigger the celebration.
    var result = Results.Json(new
    {
        five_hour = new { utilization = fakeUsage.Session, resets_at = fakeUsage.SessionResetsAt },
        seven_day = new { utilization = fakeUsage.Weekly, resets_at = fakeUsage.WeeklyResetsAt },
    });

    if (pendingCelebration)
    {
        // The monitor just saw the pre-reset usage; the next poll sees a fresh
        // window. Bump the poke counter so a watching monitor polls again now.
        pendingCelebration = false;
        fakeUsage.Session = 0;
        fakeUsage.SessionResetsAt = DateTimeOffset.UtcNow.AddSeconds(18000);
        pokeAllowsCelebration = true;
        Interlocked.Increment(ref pokeSeq);
    }

    return result;
});

app.MapPost("/scenario", (string name) =>
{
    outage = false;
    pendingCelebration = false;
    live = false;
    switch (name)
    {
        // Scenario changes carry a non-celebration poke, so they may move the
        // window times freely without the monitor reading them as resets.
        case "live": live = true; break;
        case "normal":
            fakeUsage.Session = 42; fakeUsage.Weekly = 15;
            fakeUsage.SessionResetsAt = DateTimeOffset.UtcNow.AddHours(3);
            fakeUsage.WeeklyResetsAt = DateTimeOffset.UtcNow.AddDays(4.5);
            break;
        case "panic":
            fakeUsage.Session = 92; fakeUsage.Weekly = 75;
            fakeUsage.SessionResetsAt = DateTimeOffset.UtcNow.AddMinutes(30);
            fakeUsage.WeeklyResetsAt = DateTimeOffset.UtcNow.AddDays(1);
            break;
        case "sad":
            fakeUsage.Session = 100; fakeUsage.Weekly = 100;
            fakeUsage.SessionResetsAt = DateTimeOffset.UtcNow.AddMinutes(20);
            fakeUsage.WeeklyResetsAt = DateTimeOffset.UtcNow.AddHours(6);
            break;
        case "celebrate": fakeUsage.Session = 80; pendingCelebration = true; break;
        case "outage": outage = true; break;
        default: return Results.BadRequest();
    }

    pokeAllowsCelebration = false;
    Interlocked.Increment(ref pokeSeq);
    return Results.Ok(name);
});
app.MapPost("/fake-usage", (FakeUsageUpdate update) =>
{
    fakeUsage.Session = update.Session;
    fakeUsage.Weekly = update.Weekly;
    fakeUsage.SessionResetsAt = DateTimeOffset.UtcNow.AddSeconds(update.SessionResetsInSeconds);
    fakeUsage.WeeklyResetsAt = DateTimeOffset.UtcNow.AddSeconds(update.WeeklyResetsInSeconds);
    Interlocked.Increment(ref pokeSeq);
    return Results.Ok(fakeUsage);
});

app.MapPost("/edit", () => Results.Ok());

var melodySeq = 0;
var melody = "";
app.MapPost("/api/rtttl", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    melody = await reader.ReadToEndAsync();
    Interlocked.Increment(ref melodySeq);
    return Results.Ok();
});
app.MapGet("/rtttl-last", () => Results.Json(new { seq = melodySeq, rtttl = melody }));

app.MapGet("/apps", () => Results.Json(apps));

app.MapGet("/", () => Results.Content(Page, "text/html"));

app.Run("http://localhost:8090");

class FakeUsage
{
    public double Session { get; set; } = 40;
    public double Weekly { get; set; } = 10;

    // Defaults sit at the theoretical maximum window end so that switching to
    // LIVE can never present a later resets_at than the fake state had —
    // a later timestamp plus lower usage would read as a window reset and
    // falsely trigger the celebration.
    public DateTimeOffset SessionResetsAt { get; set; } = DateTimeOffset.UtcNow.AddHours(5);
    public DateTimeOffset WeeklyResetsAt { get; set; } = DateTimeOffset.UtcNow.AddDays(7);
}

record SwitchRequest(string Name);

class FakeUsageUpdate
{
    public double Session { get; set; }
    public double Weekly { get; set; }
    public int SessionResetsInSeconds { get; set; }
    public int WeeklyResetsInSeconds { get; set; }
}

partial class Program
{
    const string Page = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>AWTRIX Simulator</title>
<style>
  body { background: #1b1b1f; color: #ccc; font-family: system-ui, sans-serif;
         display: flex; flex-direction: column; align-items: center; gap: 16px; padding: 32px; }
  .shell { background: linear-gradient(180deg, #f4f4f6, #d4d4d9 55%, #b9b9bf); border-radius: 22px;
           padding: 16px 20px; box-shadow: 0 10px 34px #000c, inset 0 1px 0 #ffffffcc; }
  .screen { background: #07080c; border-radius: 10px; padding: 14px 16px;
            box-shadow: inset 0 2px 8px #000c; }
  canvas { image-rendering: pixelated; display: block; }
  #status { font-size: 14px; color: #888; }
  #status b { color: #ddd; }
  button { font-size: 20px; padding: 7px 12px; border-radius: 8px; border: none;
           background: #333; color: #ddd; cursor: pointer; }
</style>
</head>
<body>
<div style="display:flex;gap:12px;align-items:center">
  <button id="prev">◀</button>
  <div class="shell"><div class="screen"><canvas id="matrix" width="320" height="80" style="width: 640px; height: 160px;"></canvas></div></div>
  <button id="next">▶</button>
</div>
<div id="scenarios" style="display:flex;gap:8px;flex-wrap:wrap;justify-content:center"></div>
<div id="hint" style="font-size:12px;color:#666"></div>
<div id="status">waiting for apps…</div>
<script>
const F = {
  '0':['111','101','101','101','111'],'1':['010','110','010','010','111'],
  '2':['111','001','111','100','111'],'3':['111','001','111','001','111'],
  '4':['101','101','111','001','001'],'5':['111','100','111','001','111'],
  '6':['111','100','111','101','111'],'7':['111','001','010','010','010'],
  '8':['111','101','111','101','111'],'9':['111','101','111','001','111'],
  ':':['0','1','0','1','0'],' ':['0','0','0','0','0'],
  '%':['11001','11010','00100','01011','10011'],
  '!':['1','1','1','0','1'],
};

const grid = () => Array.from({length:8},()=>Array(32).fill(null));
const text = (g,x,y,str,color)=>{ let cx=x; for(const ch of str){ const gl=F[ch]||F[' '];
  gl.forEach((r,dy)=>[...r].forEach((b,dx)=>{ if(b==='1' && cx+dx>=0 && cx+dx<32) g[y+dy][cx+dx]=color; }));
  cx+=gl[0].length+1; } };
const bar = (g,pct,lit,dim)=>{ const n=Math.round(Math.max(0,Math.min(100,pct))/100*32);
  for(let x=0;x<32;x++) g[7][x]= x<n?lit:dim; };
const iconSpr = (g,rows,map,dx=0)=>rows.forEach((r,ry)=>[...r].forEach((ch,rx)=>{
  if(map[ch] && rx+1+dx<9 && ry+1<8) g[ry+1][rx+1+dx]=map[ch];}));

// Local playback of the GIF icons the worker uploads at startup —
// this is what the real clock animates on its own between data pushes.
const ORANGE='#ff7a3d', EYE='#2ea8ff';
const body8 = (eyes,legs)=>['.OOOOOO.',eyes,'OOOOOOOO','.OOOOOO.',legs];
const ICONS = {
  clawd: (g,t)=>{ const f = t%8;
    const eyes = f===2 ? '.OOOOOO.' : (f===4||f===5) ? '.OOBOOB.' : '.OBOOBO.';
    iconSpr(g,body8(eyes,t%2?'.O....O.':'..O..O..'),{O:ORANGE,B:EYE}); },
  clawd_panic: (g)=>{ const tp = Math.floor(Date.now()/300);
    iconSpr(g,body8('.OWOOWO.',tp%2?'.O....O.':'..O..O..'),{O:ORANGE,W:'#e9e9ed'},tp%4>=1&&tp%4<=2?1:0); },
  clawd_sad: (g,t)=>{ iconSpr(g,body8('.OBOOBO.','..O..O..'),{O:ORANGE,B:'#1d6fa8'});
    if(t%4<3 && 2+(t%4)<7) g[2+(t%4)][6]='#59d0ff'; },
  clawd_grey: (g)=>{ iconSpr(g,body8('.OBOOBO.','..O..O..'),{O:'#4a4f5e',B:'#4a4f5e'}); },
};

const canvas = document.getElementById("matrix");
const ctx = canvas.getContext("2d");
let apps = {};
let names = [];
let current = 0;

function px(x, y, color) {
  ctx.fillStyle = color;
  ctx.fillRect(x * 10 + 1, y * 10 + 1, 8, 8);
}

function drawApp(a) {
  ctx.fillStyle = "#000";
  ctx.fillRect(0, 0, 320, 80);
  if (!a) return;
  if (a.icon) {
    const g = grid();
    const t = Math.floor(Date.now() / 800);
    (ICONS[a.icon] ?? (()=>{}))(g, t);
    for (const cmd of a.draw ?? [])
      if (cmd.dp) g[cmd.dp[1]][cmd.dp[0]] = cmd.dp[2];
    const textVisible = !a.blinkText || Math.floor(Date.now() / a.blinkText) % 2 === 0;
    if (a.text && textVisible) text(g, 9 + (a.textOffset ?? 0), 1, a.text, a.color ?? "#fff");
    if (a.progress != null) bar(g, a.progress, a.progressC ?? "#0f0", a.progressBC ?? "#111");
    for (let y = 0; y < 8; y++)
      for (let x = 0; x < 32; x++)
        if (g[y][x]) px(x, y, g[y][x]);
    return;
  }
  for (const cmd of a.draw ?? []) {
    if (cmd.dp) px(cmd.dp[0], cmd.dp[1], cmd.dp[2]);
  }
}

function render() {
  const count = Math.max(1, names.length);
  const idx = ((current % count) + count) % count;
  const name = names[idx];
  drawApp(apps[name]);
  document.getElementById("status").innerHTML = names.length
    ? `showing <b>${name}</b> (${idx + 1}/${names.length}) — ◀ ▶ switch screens`
    : "no apps pushed yet — start ClaudeCodeMonitor with AwtrixHost = localhost:8090";
}

setInterval(async () => {
  apps = await (await fetch("/apps")).json();
  names = Object.keys(apps).sort();
  const shown = (await (await fetch("/api/stats")).json()).app;
  if (names.includes(shown)) current = names.indexOf(shown);
  render();
}, 250);

setInterval(render, 800);

// Buzzer emulation: play RTTTL melodies posted to /api/rtttl via WebAudio.
// Browsers require a user gesture before audio — any click/keypress arms it.
let audioCtx = null;
const armAudio = () => { if (!audioCtx) audioCtx = new AudioContext(); };
document.addEventListener("click", armAudio);
document.addEventListener("keydown", armAudio);

const NOTE = {c:261.63,"c#":277.18,d:293.66,"d#":311.13,e:329.63,f:349.23,
  "f#":369.99,g:392.0,"g#":415.3,a:440.0,"a#":466.16,b:493.88};

function playRtttl(rtttl) {
  if (!audioCtx) return;
  const parts = rtttl.split(":");
  if (parts.length < 3) return;
  const def = Object.fromEntries(parts[1].split(",").map(kv => kv.split("=")));
  const dDef = Number(def.d ?? 4), oDef = Number(def.o ?? 6), bpm = Number(def.b ?? 63);
  const whole = (60 / bpm) * 4;
  let at = audioCtx.currentTime;
  for (const raw of parts[2].split(",")) {
    const m = raw.trim().toLowerCase().match(/^(\d+)?(p|[a-g]#?)(\.)?(\d)?(\.)?$/);
    if (!m) continue;
    let dur = whole / Number(m[1] ?? dDef);
    if (m[3] || m[5]) dur *= 1.5;
    if (m[2] !== "p") {
      const freq = NOTE[m[2]] * Math.pow(2, Number(m[4] ?? oDef) - 4);
      const osc = audioCtx.createOscillator();
      const gain = audioCtx.createGain();
      osc.type = "square";
      osc.frequency.value = freq;
      gain.gain.value = 0.06;
      osc.connect(gain).connect(audioCtx.destination);
      osc.start(at);
      osc.stop(at + dur * 0.9);
    }
    at += dur;
  }
}

let melodySeq = null;
setInterval(async () => {
  const last = await (await fetch("/rtttl-last")).json();
  if (melodySeq !== null && last.seq !== melodySeq && last.rtttl) playRtttl(last.rtttl);
  melodySeq = last.seq;
}, 1000);

// Test scenarios — drive the fake usage endpoint. The monitor must be started
// with Monitor__UsageBaseUrl = http://localhost:8090 for these to have effect,
// and changes are picked up on its next poll.
const SCENARIOS = {
  "LIVE": "live",
  "Normal 42%": "normal",
  "Panic 92%": "panic",
  "Sad 100%": "sad",
  "Refill celebration": "celebrate",
  "Outage": "outage",
};
const scenarios = document.getElementById("scenarios");
for (const [label, name] of Object.entries(SCENARIOS)) {
  const b = document.createElement("button");
  b.textContent = label;
  b.style.fontSize = "14px";
  b.onclick = async () => {
    await fetch("/scenario?name=" + name, { method: "POST" });
    document.getElementById("hint").textContent =
      name === "celebrate"
        ? "staged: next poll sees 80%, the one after triggers confetti + fanfare (needs Monitor__UsageBaseUrl=http://localhost:8090)"
        : name === "live"
        ? "LIVE: proxying your real Anthropic usage data — shown after the monitor's next poll"
        : `"${label}" staged — shown after the monitor's next poll (needs Monitor__UsageBaseUrl=http://localhost:8090)`;
  };
  scenarios.appendChild(b);
}

const step = d => {
  if (!names.length) return;
  current = (((current + d) % names.length) + names.length) % names.length;
  fetch("/api/switch", { method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name: names[current] }) });
  render();
};
document.getElementById("prev").onclick = () => step(-1);
document.getElementById("next").onclick = () => step(1);
document.addEventListener("keydown", e => {
  if (e.key === "ArrowLeft") step(-1);
  if (e.key === "ArrowRight") step(1);
});
</script>
</body>
</html>
""";
}
