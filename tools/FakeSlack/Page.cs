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
  .message .save { float: right; font: 12px inherit; background: #2c2d30; color: var(--text); border: 1px solid var(--line);
         border-radius: 6px; padding: 2px 8px; cursor: pointer; }
  .message .extras { color: var(--muted); font-size: 13px; }
  #modal { position: fixed; inset: 0; background: rgba(0, 0, 0, .6); display: none; align-items: center; justify-content: center; padding: 16px; }
  #modal.open { display: flex; }
  #modal form { background: var(--bg); border: 1px solid var(--line); border-radius: 10px; width: min(520px, 100%); padding: 20px;
         display: grid; gap: 12px; grid-template-columns: 1fr; }
  #modal h2 { margin: 0; font-size: 18px; }
  #modal label { display: grid; gap: 4px; font-weight: 600; }
  #modal select, #modal input { font: inherit; font-weight: 400; background: #222529; color: var(--text); border: 1px solid var(--line); border-radius: 6px; padding: 8px; }
  #modal .actions { display: flex; justify-content: flex-end; gap: 8px; }
  #modal .actions button { font: inherit; border-radius: 6px; padding: 6px 14px; cursor: pointer; border: 1px solid var(--line); background: #2c2d30; color: var(--text); }
  #modal .actions button.primary { background: #007a5a; border-color: #007a5a; color: #fff; }
  .with-image { display: flex; align-items: center; gap: 4px; font-size: 13px; color: var(--muted); }
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
  #compose { border-top: 1px solid var(--line); padding: 12px 20px; display: grid; grid-template-columns: auto 1fr auto auto auto; gap: 8px; }
  #compose select, #compose input, #compose button { font: inherit; background: #222529; color: var(--text); border: 1px solid var(--line); border-radius: 6px; padding: 8px; }
  #compose button { background: #007a5a; border-color: #007a5a; color: #fff; cursor: pointer; }
  #compose button.secondary { background: #2c2d30; border-color: var(--line); }
  @media (max-width: 700px) { body { grid-template-columns: 1fr; } aside { display: none; } #compose { grid-template-columns: 1fr; } }
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
    <label class="with-image" title="Posts a small test PNG with the message, for Save to PrikBoard"><input type="checkbox" id="image"> image</label>
    <button type="button" class="secondary" id="type" title="Sends link_shared with source composer, as Slack does while you type">Type only</button>
    <button>Send</button>
  </form>
</main>
<div id="modal" role="dialog" aria-modal="true"><form id="view"></form></div>
<script>
let state = null, channel = 'C0GENERAL1', shownView = null;
// A 1×1 PNG, for trying images on saved messages.
const TEST_PNG = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';
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
    const fromBot = m.user === state?.botUserId;
    return `<div class="message">${fromBot ? '' : `<button class="save" data-ts="${m.ts}" title="More actions → Save to PrikBoard, as the person picked below">Save to PrikBoard</button>`}
      <span class="who">${esc(users[m.user] || m.user)}</span><span class="ts">${m.ts}</span>
      <div class="text">${esc(m.text)}</div>
      ${m.files.length || m.reactions.length ? `<div class="extras">${m.files.map(f => '📎 ' + esc(f)).join(' ')} ${m.reactions.includes('pushpin') ? '📌' : ''}</div>` : ''}
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
  const views = await (await fetch('/_fake/views?user=' + $('user').value)).json();
  showView(views[0] || null);
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

// A modal the app opened for the person picked below, drawn as a form: sections, selects (with option groups) and text inputs.
function showView(v) {
  if ((v && v.id) === (shownView && shownView.id)) return;
  shownView = v;
  $('modal').classList.toggle('open', !!v);
  if (!v) return;
  const view = v.view;
  const field = b => {
    const e = b.element;
    if (e.type === 'static_select') {
      const initial = e.initial_option && e.initial_option.value;
      const option = o => `<option value="${esc(o.value)}"${o.value === initial ? ' selected' : ''}>${esc(o.text.text)}</option>`;
      const inner = e.option_groups
        ? e.option_groups.map(g => `<optgroup label="${esc(g.label.text)}">${g.options.map(option).join('')}</optgroup>`).join('')
        : (e.options || []).map(option).join('');
      return `<label>${esc(b.label.text)}<select name="${esc(b.block_id)}">${inner}</select></label>`;
    }
    return `<label>${esc(b.label.text)}<input name="${esc(b.block_id)}" value="${esc(e.initial_value || '')}" maxlength="${e.max_length || 3000}"></label>`;
  };
  $('view').innerHTML = `<h2>${esc(view.title.text)}</h2>
    ${view.blocks.map(b => b.type === 'input' ? field(b) : block(b)).join('')}
    <div class="actions"><button type="button" id="close-view">${esc(view.close ? view.close.text : 'Close')}</button>
    ${view.submit ? `<button class="primary">${esc(view.submit.text)}</button>` : ''}</div>`;
  $('close-view').onclick = () => fetch(`/_fake/views/${v.id}/close`, { method: 'POST' }).then(() => showView(null));
}

$('view').onsubmit = async e => {
  e.preventDefault();
  const values = Object.fromEntries(new FormData($('view')).entries());
  await fetch(`/_fake/views/${shownView.id}/submit`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ values }) });
  showView(null);
};

$('messages').onclick = async e => {
  const button = e.target.closest('button.save');
  if (!button) return;
  const r = await fetch('/_fake/shortcuts', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ channel, ts: button.dataset.ts, user: $('user').value }) });
  if (!r.ok) alert('PrikBoard isn\'t connected, so the shortcut went nowhere.');
};

$('compose').onsubmit = async e => {
  e.preventDefault();
  if (!$('text').value.trim()) return;
  // Like Slack, a message that starts with a slash is a command, not a message.
  const command = $('text').value.trim().startsWith('/');
  const r = await fetch(command ? '/_fake/commands' : '/_fake/messages', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ channel, user: $('user').value, text: $('text').value,
      files: $('image').checked && !command ? [{ name: 'test.png', mimetype: 'image/png', base64: TEST_PNG }] : [] }) });
  $('image').checked = false;
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
