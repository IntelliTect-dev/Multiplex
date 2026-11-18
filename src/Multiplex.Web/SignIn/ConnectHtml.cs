using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Multiplex.Web.Sites;

namespace Multiplex.Web.SignIn;

/// <summary>A form's antiforgery field, rendered into every POST form.</summary>
public sealed record FormToken(string FieldName, string Value)
{
    public string Html => $"""<input type="hidden" name="{E(FieldName)}" value="{E(Value)}">""";

    private static string E(string s) => WebUtility.HtmlEncode(s);
}

/// <summary>
/// The connect pages' HTML. Self-contained (no CDNs, fonts or external files), phone-first,
/// dark-mode aware; scripts and styles carry a per-response CSP nonce. Every dynamic value is
/// HTML-encoded; the live page's settings travel as JSON in a non-executable script block.
/// </summary>
public static class ConnectHtml
{
    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string Gate(string nonce, ISiteSignInDefinition site, PendingAuthorization pending, FormToken token, string? error)
    {
        var errorHtml = error is null ? "" : $"""<p class="error" role="alert">{E(error)}</p>""";
        return Page(nonce, $"Connect to {site.DisplayName}", $"""
            <h1>Connect {E(pending.ClientName)} to {E(site.DisplayName)}</h1>
            <p><strong>{E(pending.ClientName)}</strong> wants to connect to your <strong>{E(site.DisplayName)}</strong> account.</p>
            {(pending.ReturnsTo is { } back ? $"<p>Afterwards you go back to <strong>{E(back)}</strong>. Continue only if that is the app you are connecting.</p>" : "")}
            <form method="post" action="/connect/{E(site.Slug)}?p={Uri.EscapeDataString(pending.Id)}" class="card">
              {token.Html}
              <label for="passcode">Multiplex passcode</label>
              <input id="passcode" name="passcode" type="password" autocomplete="current-password" autocapitalize="none" autocorrect="off" spellcheck="false" required autofocus>
              {errorHtml}
              <button type="submit">Continue</button>
            </form>
            <p class="muted">Next you will sign in on {E(site.DisplayName)}'s own page, shown live here. Your {E(site.DisplayName)} password is not stored by this server.</p>
            {CancelForm(site, token, "p", pending.Id, "Not now")}
            """);
    }

    public static string Choose(string nonce, ISiteSignInDefinition site, PendingAuthorization pending, FormToken token, IReadOnlyList<SiteAccount> accounts)
    {
        var p = Uri.EscapeDataString(pending.Id);
        var buttons = string.Concat(accounts.Select(a =>
            $"""<button type="submit" name="account" value="{E(a.AccountId)}">Continue as {E(Label(a))}</button>"""));
        var continueForm = accounts.Count == 0 ? "" : $"""
            <form method="post" action="/connect/{E(site.Slug)}/continue?p={p}" class="card" data-busy="Checking your {E(site.DisplayName)} session…">
              {token.Html}
              {buttons}
            </form>
            """;
        var other = accounts.Count == 0 ? $"Sign in to {site.DisplayName}" : "Sign in with a different account";
        return Page(nonce, $"Connect to {site.DisplayName}", $"""
            <h1>Connect {E(pending.ClientName)} to {E(site.DisplayName)}</h1>
            {continueForm}
            <form method="post" action="/connect/{E(site.Slug)}/start?p={p}" data-busy="Opening {E(site.DisplayName)}…">
              {token.Html}
              <button type="submit" class="{(accounts.Count == 0 ? "" : "secondary")}">{E(other)}</button>
            </form>
            {CancelForm(site, token, "p", pending.Id, "Cancel")}
            """, BusyScript);
    }

    public static string Live(string nonce, LiveSignInAttempt attempt, FormToken token, string? notice)
    {
        var site = attempt.Definition;
        var config = JsonSerializer.Serialize(new
        {
            ws = $"/connect/{site.Slug}/live?a={Uri.EscapeDataString(attempt.Id)}",
            site = site.DisplayName,
        });
        var noticeHtml = notice is null ? "" : $"""<p class="notice" role="status">{E(notice)}</p>""";
        var hint = site.SignInHint is { } h ? $"""<p class="hint muted">{E(h)}</p>""" : "";
        return Page(nonce, $"Sign in to {site.DisplayName}", $"""
            <div id="bar" class="bar">
              <div class="row top">
                <span id="host" class="host" title="The site shown below">{E(site.DisplayName)}</span>
                {CancelForm(site, token, "a", attempt.Id, "Cancel", "cancel-inline")}
              </div>
              <p id="status" class="status" role="status" aria-live="polite">{E(attempt.StatusMessage)}</p>
              {noticeHtml}
              <div class="row type">
                <input id="txt" type="text" maxlength="500" autocomplete="off" autocapitalize="none" autocorrect="off" spellcheck="false" enterkeyhint="send" placeholder="Tap a field below, then type here" aria-label="Text to type into the {E(site.DisplayName)} page">
                <button id="send" type="button">Send</button>
              </div>
              <div class="row keys">
                <button type="button" data-key="Enter" aria-label="Press Enter">Enter</button>
                <button type="button" data-key="Backspace" aria-label="Delete one character">Delete</button>
                <button type="button" data-key="Tab" aria-label="Press Tab">Tab</button>
                <button type="button" id="hide" aria-pressed="false">Hide text</button>
                <button type="button" id="back">Back</button>
                <button type="button" id="reload">Reload</button>
              </div>
              <div id="toast" class="toast" role="alert" hidden></div>
            </div>
            <div id="stage" class="stage"><img id="screen" alt="Live view of the {E(site.DisplayName)} sign-in page" draggable="false"><span class="loading">Loading {E(site.DisplayName)}…</span></div>
            <div id="end" class="card" hidden>
              <p id="end-msg"></p>
              <a id="end-return" class="button" hidden>Return to the app</a>
              <form id="end-restart" method="post" action="/connect/{E(site.Slug)}/start?p={Uri.EscapeDataString(attempt.PendingId)}" hidden>
                {token.Html}
                <button type="submit">Start again</button>
              </form>
            </div>
            {hint}
            <script type="application/json" id="cfg">{config}</script>
            """, LiveScript, bodyClass: "live");
    }

    public static string Message(string nonce, string title, string message, string? linkUrl = null, string? linkText = null)
    {
        var link = linkUrl is null ? "" : $"""<a class="button" href="{E(linkUrl)}">{E(linkText ?? "Return to the app")}</a>""";
        return Page(nonce, title, $"""
            <h1>{E(title)}</h1>
            <p>{E(message)}</p>
            {link}
            """);
    }

    public static string Busy(string nonce, ISiteSignInDefinition site, string pendingId, FormToken token) =>
        Page(nonce, "Server busy", $"""
            <h1>Please try again in a minute</h1>
            <p>This server is already helping other people sign in. Try again shortly.</p>
            <form method="post" action="/connect/{E(site.Slug)}/start?p={Uri.EscapeDataString(pendingId)}">
              {token.Html}
              <button type="submit">Try again</button>
            </form>
            {CancelForm(site, token, "p", pendingId, "Cancel")}
            """);

    private static string Label(SiteAccount account) =>
        !string.IsNullOrWhiteSpace(account.DisplayName) ? account.DisplayName
        : account.Emails.FirstOrDefault() ?? account.AccountId;

    private static string CancelForm(ISiteSignInDefinition site, FormToken token, string key, string value, string label, string cssClass = "") => $"""
        <form method="post" action="/connect/{E(site.Slug)}/cancel?{key}={Uri.EscapeDataString(value)}" class="cancel {cssClass}">
          {token.Html}
          <button type="submit" class="link">{E(label)}</button>
        </form>
        """;

    private static string Page(string nonce, string title, string body, string? script = null, string bodyClass = "")
    {
        var html = new StringBuilder();
        html.Append($"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
            <meta name="color-scheme" content="light dark">
            <meta name="referrer" content="no-referrer">
            <title>{E(title)} · Multiplex</title>
            <style nonce="{nonce}">{Css}</style>
            </head>
            <body class="{bodyClass}">
            <main>
            {body}
            </main>
            """);
        if (script is not null) html.Append($"""<script nonce="{nonce}">{script}</script>""");
        html.Append("</body></html>");
        return html.ToString();
    }

    private const string Css = """
        :root{--bg:#f6f7f9;--fg:#14171a;--muted:#59626e;--card:#fff;--border:#d4d9e0;--accent:#1f5fd6;--accent-fg:#fff;--danger:#b42318;--notice:#7a4b00}
        @media (prefers-color-scheme:dark){:root{--bg:#0f1115;--fg:#e8eaed;--muted:#a0a8b3;--card:#191c22;--border:#2c313a;--accent:#5b95ff;--accent-fg:#0b0d10;--danger:#ff8a80;--notice:#f0c36d}}
        *{box-sizing:border-box}
        [hidden]{display:none!important}
        html,body{margin:0;padding:0;background:var(--bg);color:var(--fg);font:16px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;-webkit-text-size-adjust:100%}
        body{overflow-x:hidden}
        main{max-width:34rem;margin:0 auto;padding:max(16px,env(safe-area-inset-top)) 16px max(24px,env(safe-area-inset-bottom))}
        h1{font-size:1.35rem;line-height:1.25;margin:.5rem 0 1rem}
        p{margin:.5rem 0}
        .muted{color:var(--muted)}
        .card{background:var(--card);border:1px solid var(--border);border-radius:14px;padding:16px;margin:12px 0}
        label{display:block;font-weight:600;margin:0 0 6px}
        input[type=password],input[type=text]{width:100%;font-size:18px;padding:11px 12px;border-radius:10px;border:1px solid var(--border);background:var(--bg);color:var(--fg);min-height:48px}
        button,.button{display:block;width:100%;min-height:48px;font:inherit;font-size:17px;font-weight:600;border-radius:10px;border:1px solid transparent;padding:11px 14px;margin:10px 0 0;cursor:pointer;background:var(--accent);color:var(--accent-fg);text-align:center;text-decoration:none;touch-action:manipulation}
        button.secondary,.button.secondary{background:transparent;color:var(--fg);border-color:var(--border)}
        button.link{background:transparent;color:var(--accent);border:0;font-weight:500;width:auto;margin:6px auto 0;min-height:44px}
        button[disabled]{opacity:.6}
        .cancel{text-align:center}
        .error{color:var(--danger);font-weight:600}
        .notice{color:var(--notice);font-weight:600}
        body.live main{max-width:none;padding:0}
        .bar{position:sticky;top:0;z-index:2;background:var(--bg);padding:max(8px,env(safe-area-inset-top)) 10px 6px;border-bottom:1px solid var(--border)}
        .row{display:flex;gap:6px;align-items:center}
        .row.top{justify-content:space-between}
        .host{font-weight:600;font-size:15px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;padding:2px 10px;border:1px solid var(--border);border-radius:999px;max-width:70%}
        .host.insecure{border-color:var(--danger);color:var(--danger)}
        .cancel-inline{margin:0}
        .cancel-inline button.link{margin:0;padding:6px 10px;min-height:40px}
        .status{margin:4px 2px;font-size:15px}
        .row.type input{flex:1;min-width:0}
        .row.type button{width:auto;margin:0;min-width:5rem}
        .row.keys{flex-wrap:wrap;margin-top:6px}
        .row.keys button{flex:1 1 30%;width:auto;margin:0;min-height:44px;font-size:15px;padding:8px 6px;background:var(--card);color:var(--fg);border-color:var(--border)}
        .row.keys button[aria-pressed=true]{background:var(--accent);color:var(--accent-fg)}
        .toast{margin-top:6px;padding:8px 10px;border-radius:8px;background:var(--card);border:1px solid var(--danger);color:var(--danger);font-size:14px}
        .stage{position:relative;width:100%;min-height:240px;background:var(--card);touch-action:none;user-select:none;-webkit-user-select:none}
        .stage img{display:block;width:100%;max-width:100%;height:auto;margin:0 auto;touch-action:none;-webkit-touch-callout:none}
        .stage .loading{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;color:var(--muted)}
        .stage.has-frame .loading{display:none}
        body.live #end,body.live .hint{margin:12px 10px}
        """;

    private const string BusyScript = """
        document.querySelectorAll('form[data-busy]').forEach(function (f) {
          f.addEventListener('submit', function (e) {
            var b = e.submitter || f.querySelector('button');
            if (!b) return;
            setTimeout(function () { f.querySelectorAll('button').forEach(function (x) { x.disabled = true; }); b.textContent = f.dataset.busy; }, 0);
          });
        });
        """;

    private const string LiveScript = """
        (function () {
          var cfg = JSON.parse(document.getElementById('cfg').textContent);
          var img = document.getElementById('screen'), stage = document.getElementById('stage');
          var statusEl = document.getElementById('status'), hostEl = document.getElementById('host');
          var txt = document.getElementById('txt'), toastEl = document.getElementById('toast');
          var ws = null, finished = false, lastUrl = null, sentW = 0, retry = 0, toastTimer = null;

          function send(m) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(m)); }
          function toast(text) {
            toastEl.textContent = text; toastEl.hidden = false;
            clearTimeout(toastTimer); toastTimer = setTimeout(function () { toastEl.hidden = true; }, 5000);
          }
          function area() {
            var top = document.getElementById('bar').getBoundingClientRect().bottom;
            return { w: Math.round(stage.clientWidth), h: Math.round(window.innerHeight - Math.max(0, top) - 4) };
          }
          function reportSize(force) {
            var s = area();
            if (force || Math.abs(s.w - sentW) > 4) { sentW = s.w; send({ t: 'viewport', w: s.w, h: s.h }); }
          }
          function connect() {
            ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + cfg.ws);
            ws.binaryType = 'blob';
            ws.onopen = function () { retry = 0; reportSize(true); };
            ws.onmessage = function (e) {
              if (typeof e.data !== 'string') {
                var u = URL.createObjectURL(e.data);
                img.onload = function () { if (lastUrl) URL.revokeObjectURL(lastUrl); lastUrl = u; stage.classList.add('has-frame'); };
                img.src = u;
                return;
              }
              var m; try { m = JSON.parse(e.data); } catch (_) { return; }
              if (m.t === 'status') onStatus(m);
              else if (m.t === 'url') { hostEl.textContent = (m.secure ? '' : 'Not secure: ') + m.host; hostEl.classList.toggle('insecure', !m.secure); }
              else if (m.t === 'viewport') img.style.width = m.w + 'px';
              else if (m.t === 'blocked') toast('Blocked a link to ' + m.host + '. Only ' + cfg.site + ' sign-in pages open here.');
            };
            ws.onclose = function (e) {
              if (finished) return;
              if (e.code === 4000) { finished = true; statusEl.textContent = 'This sign-in is now showing in another tab or window.'; return; }
              statusEl.textContent = 'Reconnecting…';
              clearTimeout(reconnectTimer);
              reconnectTimer = setTimeout(connect, Math.min(10000, 1000 * (++retry)));
            };
          }
          var reconnectTimer = null;
          // Back from another app (e.g. after reading a code): reconnect straight away.
          document.addEventListener('visibilitychange', function () {
            if (document.visibilityState === 'visible' && !finished && ws && ws.readyState === 3) {
              clearTimeout(reconnectTimer); connect();
            }
          });
          function onStatus(m) {
            statusEl.textContent = m.msg;
            if ((m.s === 'ready' || m.s === 'cancelled') && m.next) { finished = true; location.replace(m.next); return; }
            if (m.s === 'timeout' || m.s === 'failed' || m.s === 'cancelled') {
              // A cancellation's way back to the app may follow a moment later; keep listening for it.
              var waitForLink = m.s === 'cancelled' && !m.next;
              finished = !waitForLink;
              document.getElementById('end-msg').textContent = m.msg;
              var ret = document.getElementById('end-return');
              if (m.next) { ret.href = m.next; ret.hidden = false; }
              if (m.s === 'timeout' || m.s === 'failed') document.getElementById('end-restart').hidden = false;
              document.getElementById('end').hidden = false;
              stage.hidden = true;
              document.querySelectorAll('.row.type, .row.keys').forEach(function (r) { r.hidden = true; });
              if (!waitForLink) { try { ws.close(); } catch (_) { } }
            }
          }

          function frac(e) {
            var r = img.getBoundingClientRect();
            return { x: Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)), y: Math.min(1, Math.max(0, (e.clientY - r.top) / r.height)), r: r };
          }
          var start = null, lastX = 0, lastY = 0, moved = false, pdx = 0, pdy = 0, flush = null;
          stage.addEventListener('pointerdown', function (e) {
            if (!e.isPrimary || !stage.classList.contains('has-frame')) return;
            start = frac(e); lastX = e.clientX; lastY = e.clientY; moved = false;
            try { stage.setPointerCapture(e.pointerId); } catch (_) { }
            e.preventDefault();
          });
          stage.addEventListener('pointermove', function (e) {
            if (!start || !e.isPrimary) return;
            var r = start.r;
            if (!moved && Math.hypot(e.clientX - (r.left + start.x * r.width), e.clientY - (r.top + start.y * r.height)) > 10) moved = true;
            if (!moved) return;
            pdx += (lastX - e.clientX) / r.width; pdy += (lastY - e.clientY) / r.height;
            lastX = e.clientX; lastY = e.clientY;
            if (!flush) flush = setTimeout(function () {
              send({ t: 'scroll', x: start ? start.x : 0.5, y: start ? start.y : 0.5, dx: pdx, dy: pdy });
              pdx = pdy = 0; flush = null;
            }, 50);
          });
          stage.addEventListener('pointerup', function (e) {
            if (!start) return;
            if (!moved) { var f = frac(e); send({ t: 'tap', x: f.x, y: f.y }); }
            start = null;
          });
          stage.addEventListener('pointercancel', function () { start = null; });
          stage.addEventListener('wheel', function (e) {
            e.preventDefault();
            var f = frac(e);
            send({ t: 'scroll', x: f.x, y: f.y, dx: e.deltaX / f.r.width, dy: e.deltaY / f.r.height });
          }, { passive: false });

          function sendText() { if (txt.value) { send({ t: 'text', v: txt.value }); txt.value = ''; } }
          var sendBtn = document.getElementById('send');
          // Keep the text field focused (and the phone keyboard open) when Send is tapped.
          sendBtn.addEventListener('pointerdown', function (e) { e.preventDefault(); });
          sendBtn.addEventListener('click', sendText);
          txt.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); sendText(); } });
          document.querySelectorAll('[data-key]').forEach(function (b) {
            b.addEventListener('pointerdown', function (e) { if (document.activeElement === txt) e.preventDefault(); });
            b.addEventListener('click', function () { sendText(); send({ t: 'key', k: b.dataset.key }); });
          });
          var hide = document.getElementById('hide');
          hide.addEventListener('click', function () {
            var on = txt.type !== 'password';
            txt.type = on ? 'password' : 'text';
            hide.setAttribute('aria-pressed', on ? 'true' : 'false');
            hide.textContent = on ? 'Show text' : 'Hide text';
          });
          document.getElementById('back').addEventListener('click', function () { send({ t: 'back' }); });
          document.getElementById('reload').addEventListener('click', function () { send({ t: 'reload' }); });
          window.addEventListener('resize', function () { reportSize(false); });
          connect();
        })();
        """;
}
