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
  if (name === 'double' && devCommandsEnabled()) {
    toggleBodyDouble(actorId);
    return;
  }
  // Reopens character creation (RaceMenu) for your existing character
  if (name === 'racemenu' && devCommandsEnabled()) {
    mp.setRaceMenuOpen(actorId, true);
    return;
  }
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

// ---------------------------------------------------------------------------
// RaceMenu body sync
// ---------------------------------------------------------------------------
//
// SkyMP syncs only the vanilla appearance. RaceMenu's body morphs (CBBE and
// HIMBO sliders) and Body Scales (height, head, breasts, butt, biceps) live in
// SKEE and the SKSE co-save, which SkyMP never loads, so they'd reset on relog
// and other players would see default bodies.
//
//   RaceSex menu open --read every 250ms--> closes: ctx.sendEvent(last reading)
//   --> mp._rpBodySave stores it as the actor's rpBody property
//   --> updateOwner / updateNeighbor apply it on every client
//
// Scales are the bones' actual sizes, read and set with SKSE's NetImmerse, not
// RaceMenu's NiOverride entries: NiOverride lookups by key fail after a few
// calls through callNative (seemingly a reference count bug with strings the
// game rarely uses, like RaceMenu's "RSMPlugin" key), while bone names are held
// by every skeleton. Bone sizes reset when the model rebuilds (gear changes,
// appearance updates), so clients check them every second and set them again.
//
// Face sculpting and overlays aren't synced yet. Morphs still go through
// NiOverride and are untested in game (the female body has no morph sliders).

const MAX_BODY_MORPHS = 400;
const MAX_BODY_SCALES = 32;
const MAX_BODY_NAME_LENGTH = 64;

// The bones RaceMenu's Body Scales sliders change (RaceMenuPlugin.psc)
const BODY_SCALE_NODES = [
  'NPC', 'NPC Head [Head]',
  'NPC L Breast', 'NPC R Breast', 'NPC L Breast01', 'NPC R Breast01',
  'NPC L Butt', 'NPC R Butt',
  'NPC L UpperarmTwist1 [LUt1]', 'NPC R UpperarmTwist1 [RUt1]',
  'NPC L UpperarmTwist2 [LUt2]', 'NPC R UpperarmTwist2 [RUt2]',
];

// Allowed bone sizes, [min, max]. Height is also limited in the RaceMenu
// slider by numidium-dev/patch-racemenu-height.py; keep the two in sync.
const BODY_SCALE_DEFAULT_LIMITS = [0.1, 10];
const BODY_SCALE_LIMITS = { NPC: [0.95, 1.05] };
const clampBodyScale = (node, scale) => {
  const [min, max] = BODY_SCALE_LIMITS[node] || BODY_SCALE_DEFAULT_LIMITS;
  return Math.min(max, Math.max(min, scale));
};

// Runs in the game. Also clamps, for bodies saved before a limit was added.
const BODY_APPLY_SRC = `
  const SCALE_LIMITS = ${JSON.stringify(BODY_SCALE_LIMITS)};
  const DEFAULT_LIMITS = ${JSON.stringify(BODY_SCALE_DEFAULT_LIMITS)};
  const applyMorphs = (sp, ref, body) => {
    const call = (fn, ...args) => sp.callNative('NiOverride', fn, undefined, ...args);
    call('ClearMorphs', ref);
    for (const [name, key, value] of body.morphs) call('SetBodyMorph', ref, name, key, value);
    call('UpdateModelWeight', ref);
  };
  // Sets the bones that aren't at their synced size, in third and first person
  const applyScales = (sp, ref, body) => {
    for (const scale of body.scales) {
      if (scale.length !== 2 || typeof scale[1] !== 'number') continue; // older format
      const node = scale[0];
      const [min, max] = SCALE_LIMITS[node] || DEFAULT_LIMITS;
      const value = Math.min(max, Math.max(min, scale[1]));
      const current = sp.NetImmerse.getNodeScale(ref, node, false);
      if (!(current > 0) || Math.abs(current - value) < 0.001) continue;
      sp.NetImmerse.setNodeScale(ref, node, value, false);
      sp.NetImmerse.setNodeScale(ref, node, value, true);
    }
  };
`;

// The local player: restores the body after every login, since SkyMP loads a
// fresh save without RaceMenu's data. Hands off while RaceMenu is open and
// until the edited body comes back from the server, so the previous body
// doesn't overwrite fresh edits.
const BODY_UPDATE_OWNER = BODY_APPLY_SRC + `
  const CHECK_MS = 1000;
  const HOLD_AFTER_MENU_MS = 5000;
  const body = ctx.value;
  const state = ctx.state;
  const now = Date.now();
  if (!body || typeof body.v !== 'number' || !ctx.refr) return;
  if (ctx.sp.Ui.isMenuOpen('RaceSex Menu')) {
    state.rpBodyHoldUntil = now + HOLD_AFTER_MENU_MS;
    return;
  }
  if (!ctx.refr.is3DLoaded()) return;
  const isNew = state.rpBodyAppliedV !== body.v;
  if (!isNew && (now < (state.rpBodyHoldUntil || 0) || now < (state.rpBodyCheckAt || 0))) return;
  state.rpBodyCheckAt = now + CHECK_MS;
  if (isNew) {
    if (body.morphs.length || state.rpBodyHadMorphs) applyMorphs(ctx.sp, ctx.refr, body);
    state.rpBodyHadMorphs = body.morphs.length > 0;
    state.rpBodyAppliedV = body.v;
    state.rpBodyHoldUntil = 0;
  }
  applyScales(ctx.sp, ctx.refr, body);
`;

// Other players near us: applied per version and per reference (a respawned
// actor is a new reference), bone sizes checked every second
const BODY_UPDATE_NEIGHBOR = BODY_APPLY_SRC + `
  const CHECK_MS = 1000;
  const body = ctx.value;
  const state = ctx.state;
  const now = Date.now();
  if (!body || typeof body.v !== 'number' || !ctx.refr || !ctx.refr.is3DLoaded()) return;
  const refId = ctx.refr.getFormID();
  const isNew = state.rpBodyAppliedV !== body.v || state.rpBodyRef !== refId;
  if (!isNew && now < (state.rpBodyCheckAt || 0)) return;
  state.rpBodyCheckAt = now + CHECK_MS;
  if (isNew) {
    if (body.morphs.length) applyMorphs(ctx.sp, ctx.refr, body);
    state.rpBodyAppliedV = body.v;
    state.rpBodyRef = refId;
  }
  applyScales(ctx.sp, ctx.refr, body);
`;

// Runs once per client. Reads the player's body while RaceMenu is open and
// sends the last reading when it closes: right after closing, SkyMP re-applies
// the appearance and rebuilds the player, which resets the bone sizes.
const BODY_EVENT_SOURCE = `
  const NODES = ${JSON.stringify(BODY_SCALE_NODES)};
  const SAMPLE_MS = 250;
  let lastSample = null;
  let nextSampleAt = 0;
  let wasOpen = false;
  const readBody = () => {
    const call = (fn, ...args) => ctx.sp.callNative('NiOverride', fn, undefined, ...args);
    const player = ctx.sp.Game.getPlayer();
    const female = ctx.sp.ActorBase.from(player.getBaseObject()).getSex() === 1;
    const morphs = [];
    for (const name of call('GetMorphNames', player) || []) {
      for (const key of call('GetMorphKeys', player, name) || []) {
        const value = call('GetBodyMorph', player, name, key);
        if (typeof value === 'number' && value !== 0) morphs.push([name, key, value]);
      }
    }
    const scales = [];
    for (const node of NODES) {
      const value = ctx.sp.NetImmerse.getNodeScale(player, node, false);
      if (value > 0) scales.push([node, value]);
    }
    return { female, morphs, scales };
  };
  ctx.sp.on('update', () => {
    const open = ctx.sp.Ui.isMenuOpen('RaceSex Menu');
    if (open && Date.now() >= nextSampleAt) {
      nextSampleAt = Date.now() + SAMPLE_MS;
      lastSample = readBody();
    }
    if (!open && wasOpen && lastSample) {
      ctx.sendEvent(lastSample);
      lastSample = null;
    }
    wasOpen = open;
  });
`;

const isBodyName = (s) => typeof s === 'string' && s.length > 0 && s.length <= MAX_BODY_NAME_LENGTH;
const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

// The client sends this, so treat it as untrusted: shapes, sizes and ranges
const sanitizeBody = (data) => {
  if (!data || typeof data !== 'object' || !Array.isArray(data.morphs) || !Array.isArray(data.scales)) return null;
  const morphs = data.morphs
    .filter((m) => Array.isArray(m) && isBodyName(m[0]) && isBodyName(m[1]) && Number.isFinite(m[2]))
    .slice(0, MAX_BODY_MORPHS)
    .map(([name, key, value]) => [name, key, clamp(value, -10, 10)]);
  const scales = data.scales
    .filter((s) => Array.isArray(s) && BODY_SCALE_NODES.includes(s[0]) && Number.isFinite(s[1]))
    .slice(0, MAX_BODY_SCALES)
    .map(([node, scale]) => [node, clampBodyScale(node, scale)]);
  return { female: data.female === true, morphs, scales };
};

const saveBody = (actorId, body) => {
  const current = mp.get(actorId, 'rpBody');
  const v = current && typeof current.v === 'number' ? current.v + 1 : 1;
  mp.set(actorId, 'rpBody', { v, female: body.female, morphs: body.morphs, scales: body.scales });
};

// Dev-only (enableConsoleCommandsForAll): a copy of your character next to you
// carrying your synced body, to see what other players see without a second game
const bodyDoubles = new Map();

// Doubles are saved with the world like any actor, and this map is lost when the
// gamemode reloads, so they're tagged with a profile id no player has and
// cleaned up on load
const BODY_DOUBLE_PROFILE_ID = 0x7ffffff0;
const removeLeftoverBodyDoubles = () => {
  let removed = 0;
  // The profile index keeps ids of doubles already destroyed, so some throw
  for (const id of mp.getActorsByProfileId(BODY_DOUBLE_PROFILE_ID)) {
    try {
      mp.destroyActor(id);
      removed++;
    } catch (e) {
      // already gone
    }
  }
  if (removed) console.log(`[body] removed ${removed} leftover body double(s)`);
};

const devCommandsEnabled = () => mp.getServerSettings().enableConsoleCommandsForAll === true;

const toggleBodyDouble = (actorId) => {
  const existing = bodyDoubles.get(actorId);
  if (existing) {
    mp.destroyActor(existing);
    bodyDoubles.delete(actorId);
    system(actorId, 'Body double removed.');
    return;
  }
  const [x, y, z] = mp.getActorPos(actorId);
  const angle = mp.get(actorId, 'angle');
  const angleZ = Array.isArray(angle) ? angle[2] : 0;
  const rad = (angleZ * Math.PI) / 180;
  const pos = [x + Math.sin(rad) * 150, y + Math.cos(rad) * 150, z];
  const doubleId = mp.createActor(0, pos, angleZ + 180, mp.getActorCellOrWorld(actorId), BODY_DOUBLE_PROFILE_ID);
  mp.set(doubleId, 'appearance', mp.get(actorId, 'appearance'));
  const body = mp.get(actorId, 'rpBody');
  if (body) mp.set(doubleId, 'rpBody', body);
  // createActor doesn't put the actor on the streaming grid, so nobody would see it
  // until it moves; teleporting it in place does
  mp.set(doubleId, 'locationalData', mp.get(doubleId, 'locationalData'));
  bodyDoubles.set(actorId, doubleId);
  system(actorId, body
    ? `Body double spawned with your synced body (${body.morphs.length} morphs, ${body.scales.length} bones). /double again removes it.`
    : 'Body double spawned, but you have no synced body yet: open and close RaceMenu first.');
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

mp.makeProperty('rpBody', {
  isVisibleByOwner: true,
  isVisibleByNeighbors: true,
  updateOwner: BODY_UPDATE_OWNER,
  updateNeighbor: BODY_UPDATE_NEIGHBOR,
});

mp.makeEventSource('_rpBodySave', BODY_EVENT_SOURCE);

mp._rpBodySave = (actorId, data) => {
  try {
    const body = sanitizeBody(data);
    if (!body) {
      console.log(`[body] ignored malformed body from ${actorId.toString(16)}`);
      return;
    }
    saveBody(actorId, body);
    const changed = body.scales.filter(([, scale]) => Math.abs(scale - 1) >= 0.005)
      .map(([node, scale]) => `${node} ${scale.toFixed(2)}`);
    console.log(`[body] ${getName(actorId)} (${actorId.toString(16)}): ${body.morphs.length} morphs, `
      + `${body.scales.length} bones${changed.length ? ` (${changed.join(', ')})` : ''}`);
    const doubleId = bodyDoubles.get(actorId);
    if (doubleId) saveBody(doubleId, body);
  } catch (e) {
    console.error('[body] failed to save body', e);
  }
};

removeLeftoverBodyDoubles();

console.log('[numidium] gamemode loaded: roleplay chat, RaceMenu body sync');
