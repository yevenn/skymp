// Numidium gamemode: text roleplay chat.
//
// Flow:
//   chat widget (browser) --skyrimPlatform.sendMessage('rpchat:send')--> event source
//   --ctx.sendEvent--> mp._rpChatSend(actorId, text) on the server
//   server --mp.set(actorId, 'rpChat', { v, msgs })--> updateOwner on that player's client
//   --browser.executeJavaScript--> chat widget re-render
//
// Distances are in Skyrim units (roughly 70 units per metre).

const RANGES = {
  whisper: 150,
  say: 1000,
  shout: 3000,
  emote: 1000,
  looc: 1000,
};

const MAX_MESSAGE_LENGTH = 500;
const MAX_HISTORY = 50;

const COLORS = {
  name: '#FFFFFF',
  say: '#E6E6E6',
  whisper: '#A9A9A9',
  shout: '#FFB3B3',
  me: '#C2A2DA',
  do: '#C2A2DA',
  roll: '#D9C38A',
  looc: '#9E9E9E',
  ooc: '#8FB3FF',
  system: '#E8C547',
};

const HELP_LINES = [
  'Chat commands:',
  '  text  or  /s text — say (normal range)',
  '  /w text — whisper (very close)',
  '  /sh text — shout (far)',
  '  /me action — emote, e.g. /me draws her sword',
  '  /do description — describe the scene, e.g. /do The door is locked.',
  '  /b text — local out-of-character',
  '  /ooc text — global out-of-character',
  '  /roll [XdY] — roll dice nearby, e.g. /roll 2d6',
  'Enter opens the chat, Esc closes it.',
];

// ---------------------------------------------------------------------------
// Client side (sent to every player as strings)
// ---------------------------------------------------------------------------

// Runs inside the in-game browser. Keeps the chat widget in sync with the server.
function browserRender(data) {
  var sp = window.skyrimPlatform;
  if (!sp || !sp.widgets) return;

  var chat = window.rpChat;
  if (!chat) {
    chat = window.rpChat = { hidden: true };
    chat.render = function () {
      sp.widgets.set([{
        type: 'chat',
        messages: window.chatMessages.slice(),
        send: function (text) { sp.sendMessage('rpchat:send', text); },
        placeholder: 'Type to say something, /help for commands',
        isInputHidden: chat.hidden,
      }]);
      setTimeout(function () {
        if (window.scrollToLastMessage) window.scrollToLastMessage();
      }, 0);
    };
    window.addEventListener('skymp5-client:browserFocused', function () {
      chat.hidden = false;
      chat.render();
    });
    window.addEventListener('skymp5-client:browserUnfocused', function () {
      chat.hidden = true;
      chat.render();
    });
  }

  window.chatMessages = data.msgs;
  chat.render();
  sp.sendMessage('rpchat:rendered', data.v);
}

const BROWSER_RENDER_SRC = '(' + browserRender.toString() + ')';

// Runs every frame for the local player. Pushes new history to the browser,
// retrying once a second until the browser confirms it rendered that version.
const UPDATE_OWNER = `
  const value = ctx.value;
  if (!value || typeof value.v !== 'number') return;
  const storage = ctx.sp.storage;
  if (storage.rpChatRenderedV === value.v) return;
  const now = Date.now();
  if (storage.rpChatPushedV === value.v && now - (storage.rpChatPushedAt || 0) < 1000) return;
  storage.rpChatPushedV = value.v;
  storage.rpChatPushedAt = now;
  ctx.sp.browser.executeJavaScript(${JSON.stringify(BROWSER_RENDER_SRC)} + '(' + JSON.stringify(value) + ')');
`;

// Runs once per client, as soon as it has the gamemode. Lets the server greet the player
// so they get a chat window even before anyone has spoken.
const JOIN_EVENT_SOURCE = `
  ctx.sp.once('update', () => ctx.sendEvent());
`;

// Runs once per client. Forwards chat input to the server and tracks render acks.
const EVENT_SOURCE = `
  ctx.sp.on('browserMessage', (event) => {
    const key = event.arguments[0];
    const payload = event.arguments[1];
    if (key === 'rpchat:send' && typeof payload === 'string') {
      ctx.sendEvent(payload);
    } else if (key === 'rpchat:rendered') {
      ctx.sp.storage.rpChatRenderedV = payload;
    } else if (key === 'front-loaded') {
      // The browser page was (re)loaded, so the chat has to be drawn again
      ctx.sp.storage.rpChatRenderedV = -1;
      ctx.sp.storage.rpChatPushedV = -1;
    }
  });
`;

// ---------------------------------------------------------------------------
// Server side
// ---------------------------------------------------------------------------

const span = (text, color, nonRp) => ({
  text,
  color,
  opacity: 1,
  type: nonRp ? ['nonrp'] : ['default'],
});

const message = (spans, nonRp) => ({
  text: spans,
  category: nonRp ? 'plain' : 'rp',
  opacity: 1,
});

const getOnlineActors = () => {
  const actors = mp.get(0, 'onlinePlayers');
  return Array.isArray(actors) ? actors : [];
};

const getName = (actorId) => {
  try {
    const name = mp.getActorName(actorId);
    if (typeof name === 'string' && name.trim()) return name.trim();
  } catch (e) {
    // Fall through to the default below
  }
  return 'Stranger';
};

const isInRange = (fromId, toId, range) => {
  if (fromId === toId) return true;
  if (mp.getActorCellOrWorld(fromId) !== mp.getActorCellOrWorld(toId)) return false;
  const a = mp.getActorPos(fromId);
  const b = mp.getActorPos(toId);
  const dx = a[0] - b[0];
  const dy = a[1] - b[1];
  const dz = a[2] - b[2];
  return dx * dx + dy * dy + dz * dz <= range * range;
};

const deliver = (actorId, msg) => {
  const current = mp.get(actorId, 'rpChat');
  const msgs = current && Array.isArray(current.msgs) ? current.msgs.slice(-(MAX_HISTORY - 1)) : [];
  msgs.push(msg);
  const v = current && typeof current.v === 'number' ? current.v + 1 : 1;
  mp.set(actorId, 'rpChat', { v, msgs });
};

const deliverNear = (fromId, range, msg) => {
  for (const actorId of getOnlineActors()) {
    if (isInRange(fromId, actorId, range)) deliver(actorId, msg);
  }
};

const deliverAll = (msg) => {
  for (const actorId of getOnlineActors()) deliver(actorId, msg);
};

const system = (actorId, text) => deliver(actorId, message([span(text, COLORS.system, true)], true));

const logChat = (channel, actorId, text) => {
  console.log(`[chat:${channel}] ${getName(actorId)} (${actorId.toString(16)}): ${text}`);
};

const ensureEnding = (text) => (/[.!?…"')\]]$/.test(text) ? text : text + '.');

const parseDice = (spec) => {
  const match = /^(\d{1,2})?d(\d{1,3})$/i.exec(spec || '1d20');
  if (!match) return null;
  const count = Math.max(1, Math.min(10, parseInt(match[1] || '1', 10)));
  const sides = Math.max(2, Math.min(1000, parseInt(match[2], 10)));
  return { count, sides };
};

const roll = (actorId, spec) => {
  const dice = parseDice(spec);
  if (!dice) {
    system(actorId, 'Usage: /roll XdY, for example /roll 1d20 or /roll 2d6');
    return;
  }
  const results = [];
  for (let i = 0; i < dice.count; i++) results.push(1 + Math.floor(Math.random() * dice.sides));
  const total = results.reduce((a, b) => a + b, 0);
  const detail = dice.count > 1 ? ` (${results.join(' + ')})` : '';
  const text = `* ${getName(actorId)} rolls ${dice.count}d${dice.sides}: ${total}${detail}`;
  logChat('roll', actorId, text);
  deliverNear(actorId, RANGES.emote, message([span(text, COLORS.roll)]));
};

const speak = (actorId, verb, text, range, color) => {
  const name = getName(actorId);
  logChat(verb, actorId, text);
  deliverNear(actorId, range, message([
    span(`${name} ${verb}: `, COLORS.name),
    span(`"${ensureEnding(text)}"`, color),
  ]));
};

const commands = {
  s: (actorId, text) => speak(actorId, 'says', text, RANGES.say, COLORS.say),
  w: (actorId, text) => speak(actorId, 'whispers', text, RANGES.whisper, COLORS.whisper),
  sh: (actorId, text) => speak(actorId, 'shouts', /[!?]$/.test(text) ? text : text.replace(/[.]$/, '') + '!', RANGES.shout, COLORS.shout),
  me: (actorId, text) => {
    logChat('me', actorId, text);
    deliverNear(actorId, RANGES.emote, message([span(`* ${getName(actorId)} ${text}`, COLORS.me)]));
  },
  do: (actorId, text) => {
    logChat('do', actorId, text);
    deliverNear(actorId, RANGES.emote, message([span(`* ${ensureEnding(text)} (( ${getName(actorId)} ))`, COLORS.do)]));
  },
  b: (actorId, text) => {
    logChat('looc', actorId, text);
    deliverNear(actorId, RANGES.looc, message([span(`(( ${getName(actorId)}: ${text} ))`, COLORS.looc, true)], true));
  },
  ooc: (actorId, text) => {
    logChat('ooc', actorId, text);
    deliverAll(message([span(`[OOC] ${getName(actorId)}: ${text}`, COLORS.ooc, true)], true));
  },
};

const aliases = {
  say: 's',
  whisper: 'w',
  shout: 'sh',
  y: 'sh',
  yell: 'sh',
  looc: 'b',
  o: 'ooc',
};

// Sent automatically by parts of the stock chat window we don't support yet
const IGNORED_COMMANDS = new Set(['skill-dice', 'skill', 'anim']);

const handleChat = (actorId, rawText) => {
  let text = String(rawText).replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim();
  if (!text) return;
  if (text.length > MAX_MESSAGE_LENGTH) text = text.slice(0, MAX_MESSAGE_LENGTH);

  if (!text.startsWith('/')) {
    commands.s(actorId, text);
    return;
  }

  const spaceIndex = text.indexOf(' ');
  const name = (spaceIndex === -1 ? text.slice(1) : text.slice(1, spaceIndex)).toLowerCase();
  const args = spaceIndex === -1 ? '' : text.slice(spaceIndex + 1).trim();

  if (IGNORED_COMMANDS.has(name)) return;
  if (name === 'help' || name === 'h') {
    for (const line of HELP_LINES) system(actorId, line);
    return;
  }
  if (name === 'roll' || name === 'r') {
    roll(actorId, args.split(' ')[0] || '1d20');
    return;
  }
  // The dice pouch in the chat window sends "/1d20", "/1d6", ...
  if (/^\d{0,2}d\d{1,3}$/.test(name)) {
    roll(actorId, name);
    return;
  }

  const command = commands[aliases[name] || name];
  if (!command) {
    system(actorId, `Unknown command /${name}. Type /help for the list.`);
    return;
  }
  if (!args) {
    system(actorId, `/${name} needs some text after it. Type /help for examples.`);
    return;
  }
  command(actorId, args);
};

mp.makeProperty('rpChat', {
  isVisibleByOwner: true,
  isVisibleByNeighbors: false,
  updateOwner: UPDATE_OWNER,
  updateNeighbor: '',
});

mp.makeEventSource('_rpChatSend', EVENT_SOURCE);
mp.makeEventSource('_rpChatJoin', JOIN_EVENT_SOURCE);

mp._rpChatSend = (actorId, text) => {
  try {
    handleChat(actorId, text);
  } catch (e) {
    console.error('[chat] failed to handle message', e);
  }
};

mp._rpChatJoin = (actorId) => {
  try {
    system(actorId, `Welcome to Numidium, ${getName(actorId)}. Press Enter to chat, /help for commands.`);
  } catch (e) {
    console.error('[chat] failed to greet player', e);
  }
};

console.log('[numidium] gamemode loaded: roleplay chat');
