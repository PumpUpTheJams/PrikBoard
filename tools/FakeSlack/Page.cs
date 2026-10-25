namespace FakeSlack;

/// <summary>
/// The fake Slack's page: the channels on the left, a channel's messages with their unfurls, and a box to post as one
/// of the workspace's people. Block Kit is drawn roughly (sections, fields, context, the color bar); each unfurl also has
/// its raw JSON and a link to Slack's Block Kit Builder for the real look.
/// </summary>
internal static class Page
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Fake Slack</title>
<style>
  :root { --bg: #1a1d21; --side: #19171d; --line: #35373b; --text: #d1d2d3; --muted: #9a9b9e; --accent: #1264a3; --link: #1d9bd1; }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.45 system-ui, sans-serif; display: grid;
         grid-template-columns: 240px 1fr; height: 100dvh; }
  aside { background: var(--side); border-right: 1px solid var(--line); padding: 16px 0; overflow: auto; }
  aside h1 { font-size: 17px; margin: 0 16px 4px; }
  aside .status { margin: 0 16px 16px; font-size: 13px; color: var(--muted); }
  aside .status b.on { color: #2bac76; } aside .status b.off { color: #e01e5a; }
  aside button.channel { display: block; width: 100%; text-align: left; background: none; border: 0; color: var(--muted);
         padding: 4px 16px; font: inherit; cursor: pointer; }
  aside button.channel.active { background: var(--accent); color: #fff; }
  aside .note { font-size: 12px; color: var(--muted); padding: 0 16px; }
  aside .tools { padding: 16px; display: grid; gap: 8px; font-size: 13px; color: var(--muted); }
  aside .tools button { font: inherit; background: #2c2d30; color: var(--text); border: 1px solid var(--line); border-radius: 6px; padding: 6px; cursor: pointer; }
  main { display: grid; grid-template-rows: auto 1fr auto; min-height: 0; }
  header { padding: 12px 20px; border-bottom: 1px solid var(--line); font-weight: 700; }
  #messages { overflow: auto; padding: 12px 20px; }
  .message { padding: 8px 0; }
  .message .who { font-weight: 700; } .message .ts { color: var(--muted); font-size: 12px; margin-left: 8px; }
  .message .text { white-space: pre-wrap; overflow-wrap: anywhere; }
  .message.ephemeral { border-left: 3px solid var(--muted); padding-left: 10px; }
  .unfurl { display: flex; margin-top: 6px; max-width: 640px; }
  .unfurl .bar { width: 4px; border-radius: 4px; flex: none; }
  .unfurl .body { padding: 2px 12px; min-width: 0; }
  .section { margin: 4px 0; white-space: pre-wrap; overflow-wrap: anywhere; }
  .fields { display: grid; grid-template-columns: 1fr 1fr; gap: 6px 16px; margin: 6px 0; }
  .context { display: flex; align-items: center; gap: 6px; color: var(--muted); font-size: 12px; }
  .context img { width: 16px; height: 16px; border-radius: 3px; }
  .raw { font-size: 12px; color: var(--muted); } .raw pre { white-space: pre-wrap; background: #111; padding: 8px; border-radius: 6px; }
  a { color: var(--link); }
  .pending { color: var(--muted); font-size: 13px; font-style: italic; }
  form { border-top: 1px solid var(--line); padding: 12px 20px; display: grid; grid-template-columns: auto 1fr auto auto; gap: 8px; }
  form select, form input, form button { font: inherit; background: #222529; color: var(--text); border: 1px solid var(--line); border-radius: 6px; padding: 8px; }
  form button { background: #007a5a; border-color: #007a5a; color: #fff; cursor: pointer; }
  form button.secondary { background: #2c2d30; border-color: var(--line); }
  @media (max-width: 700px) { body { grid-template-columns: 1fr; } aside { display: none; } form { grid-template-columns: 1fr; } }
</style>
</head>
<body>
<aside>
  <h1 id="team">Fake Slack</h1>
  <div class="status" id="status">…</div>
  <div id="channels"></div>
  <div class="tools">
    <div id="tokens"></div>
    <button id="disconnect" title="Sends disconnect (refresh_requested), as Slack does every few hours">Ask the app to reconnect</button>
  </div>
</aside>
<main>
  <header id="title">#general</header>
  <div id="messages"></div>
  <form id="compose">
    <select id="user" aria-label="Post as"></select>
    <input id="text" autocomplete="off" placeholder="Paste a PrikBoard link, or /prikboard board…" aria-label="Message">
    <button type="button" class="secondary" id="type" title="Sends link_shared with source composer, as Slack does while you type">Type only</button>
    <button>Send</button>
  </form>
</main>
<script>
let state = null, channel = 'C0GENERAL1';
const $ = id => document.getElementById(id);
const esc = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

// Slack mrkdwn, roughly: the text arrives escaped already (&amp; &lt; &gt;), so only links, dates and bold are turned into HTML.
function mrkdwn(text) {
  return text
    .replace(/<!date\^\d+\^[^|>]*\|([^>]*)>/g, (_, fallback) => fallback)
    .replace(/<([^|>]+)\|([^>]*)>/g, (_, url, label) => `<a href="${url.replace(/"/g, '&quot;')}" target="_blank" rel="noopener">${label}</a>`)
    .replace(/<(https?:[^>]+)>/g, (_, url) => `<a href="${url}" target="_blank" rel="noopener">${url}</a>`)
    .replace(/\*([^*\n]+)\*/g, '<b>$1</b>');
}

function block(b) {
  if (b.type === 'section') {
    let html = b.text ? `<div class="section">${mrkdwn(b.text.text)}</div>` : '';
    if (b.fields) html += `<div class="fields">${b.fields.map(f => `<div>${mrkdwn(f.text)}</div>`).join('')}</div>`;
    return html;
  }
  if (b.type === 'context') {
    return `<div class="context">${b.elements.map(e => e.type === 'image'
      ? `<img src="${e.image_url}" alt="${esc(e.alt_text || '')}">` : `<span>${mrkdwn(e.text)}</span>`).join('')}</div>`;
  }
  return `<div class="pending">(${esc(b.type)} block)</div>`;
}

function unfurl(url, u) {
  const builder = 'https://app.slack.com/block-kit-builder#' + encodeURIComponent(JSON.stringify({ attachments: [u] }));
  return `<div class="unfurl"><div class="bar" style="background:${u.color || '#ddd'}"></div><div class="body">
      ${(u.blocks || []).map(block).join('')}
      <details class="raw"><summary>JSON · <a href="${builder}" target="_blank" rel="noopener">Block Kit Builder</a></summary>
      <pre>${esc(JSON.stringify(u, null, 2))}</pre></details></div></div>`;
}

async function refresh() {
  const messages = await (await fetch('/_fake/messages?channel=' + channel)).json();
  const responses = await (await fetch('/_fake/responses?channel=' + channel)).json();
  const users = Object.fromEntries((state?.users || []).map(u => [u.id, u.name]));
  users[state?.botUserId] = 'PrikBoard (app)';
  const atBottom = $('messages').scrollTop + $('messages').clientHeight >= $('messages').scrollHeight - 20;
  const posted = messages.map(m => {
    const shown = Object.entries(m.unfurls);
    const waiting = m.links.filter(l => !(l in m.unfurls));
    return `<div class="message"><span class="who">${esc(users[m.user] || m.user)}</span><span class="ts">${m.ts}</span>
      <div class="text">${esc(m.text)}</div>
      ${shown.map(([url, u]) => unfurl(url, u)).join('')}
      ${(m.attachments || []).map(a => unfurl('', a)).join('')}
      ${waiting.length ? `<div class="pending">${waiting.length} link(s) without a preview (yet)</div>` : ''}</div>`;
  });
  // Answers to slash commands come last, as in Slack, where they stay at the bottom until you reload.
  const answers = responses.map(r => `<div class="message ephemeral"><span class="who">PrikBoard (app)</span>
      <span class="ts">Only visible to ${esc(users[r.user] || r.user)} · ${esc(r.command)}</span>
      <div class="text">${r.body?.attachments ? '' : mrkdwn(esc(r.body?.text || ''))}</div>
      ${(r.body?.attachments || []).map(a => unfurl('', a)).join('')}</div>`);
  $('messages').innerHTML = [...posted, ...answers].join('') || '<div class="pending">No messages yet.</div>';
  if (atBottom) $('messages').scrollTop = $('messages').scrollHeight;
  state = await (await fetch('/_fake/state')).json();
  $('status').innerHTML = state.connections ? '<b class="on">●</b> PrikBoard connected' : '<b class="off">●</b> PrikBoard not connected';
}

async function load() {
  state = await (await fetch('/_fake/state')).json();
  $('team').textContent = state.team.name;
  $('tokens').innerHTML = `Bot token <code>${esc(state.botToken)}</code><br>App token <code>${esc(state.appToken)}</code><br>Unfurl domains: ${esc(state.unfurlDomains.join(', '))}`;
  $('user').innerHTML = state.users.map(u => `<option value="${u.id}">${esc(u.name)}</option>`).join('');
  $('channels').innerHTML = state.channels.map(c =>
    `<button class="channel" data-id="${c.id}">${c.isExtShared ? '⇄' : '#'} ${esc(c.name)}${c.isMember ? '' : ' <small>(app not added)</small>'}</button>`).join('')
    + '<p class="note">⇄ is shared with another organization (Slack Connect).</p>';
  document.querySelectorAll('button.channel').forEach(b => b.onclick = () => { channel = b.dataset.id; pick(); });
  pick();
}

function pick() {
  document.querySelectorAll('button.channel').forEach(b => b.classList.toggle('active', b.dataset.id === channel));
  $('title').textContent = '#' + state.channels.find(c => c.id === channel).name;
  refresh();
}

$('compose').onsubmit = async e => {
  e.preventDefault();
  if (!$('text').value.trim()) return;
  // Like Slack, a message that starts with a slash is a command, not a message.
  const command = $('text').value.trim().startsWith('/');
  const r = await fetch(command ? '/_fake/commands' : '/_fake/messages', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ channel, user: $('user').value, text: $('text').value }) });
  if (command && !r.ok) alert('PrikBoard isn\'t connected, so the command went nowhere.');
  $('text').value = '';
  refresh();
};
$('type').onclick = () => fetch('/_fake/composer', { method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ channel, user: $('user').value, text: $('text').value }) });
$('disconnect').onclick = () => fetch('/_fake/disconnect', { method: 'POST' });

load();
setInterval(refresh, 1000);
</script>
</body>
</html>
""";
}
