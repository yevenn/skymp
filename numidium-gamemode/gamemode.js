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
  '  /names — hide or show the names above heads (or F1)',
  '  /id — your own ID',
  '  /faction (or /f) — your factions; /f help for the rest',
  'Enter opens the chat, Esc closes it.',
  'Look at someone and press X to introduce yourself. Until then, people know you by your ID.',
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

// The character's real name, kept server-side (see Identities). Their synced
// name (mp.getActorName) is their ID, which everyone may see.
const getName = (actorId) => {
  const name = mp.get(actorId, 'private.rpName');
  if (typeof name === 'string' && name.trim()) return name.trim();
  return getCharId(actorId) || 'Stranger';
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

// msg is a message, or a function making each recipient's own (names differ by
// who knows whom: see label)
const deliverNear = (fromId, range, msg) => {
  for (const actorId of getOnlineActors()) {
    if (isInRange(fromId, actorId, range)) deliver(actorId, typeof msg === 'function' ? msg(actorId) : msg);
  }
};

const deliverAll = (msg) => {
  for (const actorId of getOnlineActors()) deliver(actorId, typeof msg === 'function' ? msg(actorId) : msg);
};

const system = (actorId, text) => deliver(actorId, message([span(text, COLORS.system, true)], true));

const logChat = (channel, actorId, text) => {
  console.log(`[chat:${channel}] ${getName(actorId)} [${getCharId(actorId)}] (${actorId.toString(16)}): ${text}`);
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
  const what = `rolls ${dice.count}d${dice.sides}: ${total}${detail}`;
  logChat('roll', actorId, what);
  deliverNear(actorId, RANGES.emote, (to) => message([span(`* ${label(to, actorId)} ${what}`, COLORS.roll)]));
};

const speak = (actorId, verb, text, range, color) => {
  logChat(verb, actorId, text);
  deliverNear(actorId, range, (to) => message([
    span(`${label(to, actorId)} ${verb}: `, COLORS.name),
    span(`"${ensureEnding(text)}"`, color),
  ]));
};

const commands = {
  s: (actorId, text) => speak(actorId, 'says', text, RANGES.say, COLORS.say),
  w: (actorId, text) => speak(actorId, 'whispers', text, RANGES.whisper, COLORS.whisper),
  sh: (actorId, text) => speak(actorId, 'shouts', /[!?]$/.test(text) ? text : text.replace(/[.]$/, '') + '!', RANGES.shout, COLORS.shout),
  me: (actorId, text) => {
    logChat('me', actorId, text);
    deliverNear(actorId, RANGES.emote, (to) => message([span(`* ${label(to, actorId)} ${text}`, COLORS.me)]));
  },
  do: (actorId, text) => {
    logChat('do', actorId, text);
    deliverNear(actorId, RANGES.emote, (to) => message([span(`* ${ensureEnding(text)} (( ${label(to, actorId)} ))`, COLORS.do)]));
  },
  b: (actorId, text) => {
    logChat('looc', actorId, text);
    deliverNear(actorId, RANGES.looc, (to) => message([span(`(( ${label(to, actorId)}: ${text} ))`, COLORS.looc, true)], true));
  },
  ooc: (actorId, text) => {
    logChat('ooc', actorId, text);
    deliverAll((to) => message([span(`[OOC] ${label(to, actorId)}: ${text}`, COLORS.ooc, true)], true));
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
  if (name === 'double' && devCommandsEnabled(actorId)) {
    toggleBodyDouble(actorId);
    return;
  }
  // Reopens character creation (RaceMenu) for your existing character
  if (name === 'racemenu' && devCommandsEnabled(actorId)) {
    mp.setRaceMenuOpen(actorId, true);
    return;
  }
  if (name === 'names') {
    toggleNames(actorId);
    return;
  }
  if (name === 'faction' || name === 'f') {
    factionCommand(actorId, args);
    return;
  }
  if (name === 'tp') {
    teleportCommand(actorId, args);
    return;
  }
  if (name === 'id') {
    system(actorId, `Your ID is ${getCharId(actorId) || 'not set yet'}: what people who don't know you see over your head.`);
    return;
  }
  if (name === 'help' || name === 'h') {
    for (const line of HELP_LINES) system(actorId, line);
    if (isAdmin(actorId)) system(actorId, 'Admin: /tp [place] [ID] — teleport yourself or someone; /tp alone lists the places.');
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
// Identities: IDs until people introduce themselves
// ---------------------------------------------------------------------------
//
// Every character has a short random ID (rpTag, seen by everyone near). Their
// name, typed in RaceMenu, is kept server-side (private.rpName), and their synced
// name (appearance.name, what the game shows in "Talk ...") is set to the ID, so
// no client learns a name it wasn't told.
//
// Introducing yourself (X on someone, then the menu) adds you to what they know:
//   private.rpKnows on the viewer: form ids of the characters they know
//   private.rpKnownBy on the character: form ids of who knows them (for renames)
//   rpKnown (seen by its owner only): { formId: name } of the characters they know
// Labels over heads and in chat show the name to those who know it, the ID to the rest.

const ID_ALPHABET = '23456789ABCDEFGHJKMNPQRSTUVWXYZ'; // no 0/O, 1/I/L
const ID_LENGTH = 5;
const MAX_NAME_LENGTH = 40;
const INTRODUCE_RANGE = 500;

const getCharId = (actorId) => {
  const tag = mp.get(actorId, 'rpTag');
  return tag && typeof tag.id === 'string' ? tag.id : null;
};

const newCharId = () => {
  for (;;) {
    let id = '';
    for (let i = 0; i < ID_LENGTH; i++) id += ID_ALPHABET[Math.floor(Math.random() * ID_ALPHABET.length)];
    if (mp.findFormsByPropertyValue('private.indexed.rpCharId', id).length === 0) return id;
  }
};

const idList = (value) => (Array.isArray(value) ? value.filter((v) => typeof v === 'number') : []);

const knows = (viewerId, actorId) => viewerId === actorId || idList(mp.get(viewerId, 'private.rpKnows')).includes(actorId);

// How viewerId sees actorId: their name if introduced, else their ID
const label = (viewerId, actorId) => (knows(viewerId, actorId) ? getName(actorId) : getCharId(actorId) || 'Stranger');

// Sends viewerId the names of everyone they know
const refreshKnown = (viewerId) => {
  const known = {};
  for (const id of idList(mp.get(viewerId, 'private.rpKnows'))) known[id] = getName(id);
  mp.set(viewerId, 'rpKnown', known);
};

const learn = (viewerId, actorId) => {
  if (knows(viewerId, actorId)) return false;
  mp.set(viewerId, 'private.rpKnows', [...idList(mp.get(viewerId, 'private.rpKnows')), actorId]);
  mp.set(actorId, 'private.rpKnownBy', [...idList(mp.get(actorId, 'private.rpKnownBy')), viewerId]);
  refreshKnown(viewerId);
  return true;
};

// Gives the character an ID if it has none, and moves a name typed in RaceMenu
// (which arrives as the synced name) to private.rpName, putting the ID back
const ensureIdentity = (actorId) => {
  let id = getCharId(actorId);
  if (!id) {
    id = newCharId();
    mp.set(actorId, 'private.indexed.rpCharId', id);
    mp.set(actorId, 'rpTag', { id });
    console.log(`[identity] ${actorId.toString(16)} is ${id}`);
  }
  const appearance = mp.get(actorId, 'appearance');
  if (!appearance || typeof appearance.name !== 'string' || appearance.name === id) return;
  const typed = appearance.name.replace(/[\u0000-\u001f<>]/g, '').trim().slice(0, MAX_NAME_LENGTH);
  if (typed && typed !== mp.get(actorId, 'private.rpName')) {
    console.log(`[identity] ${id} is named ${typed}`);
    mp.set(actorId, 'private.rpName', typed);
    for (const viewerId of idList(mp.get(actorId, 'private.rpKnownBy'))) refreshKnown(viewerId);
  }
  mp.set(actorId, 'appearance', { ...appearance, name: id });
};

const isOnline = (actorId) => getOnlineActors().includes(actorId);

const introduce = (actorId, targetId) => {
  if (targetId === actorId || !isOnline(targetId) || !getCharId(targetId)) return;
  if (!isInRange(actorId, targetId, INTRODUCE_RANGE)) {
    system(actorId, `${label(actorId, targetId)} is too far away to introduce yourself.`);
    return;
  }
  const name = getName(actorId);
  const isNew = learn(targetId, actorId);
  console.log(`[identity] ${name} [${getCharId(actorId)}] introduced themselves to ${getName(targetId)} [${getCharId(targetId)}]`);
  deliverNear(actorId, RANGES.emote, (to) => {
    if (to === actorId) return message([span(`* You introduce yourself to ${label(actorId, targetId)} as ${name}.`, COLORS.me)]);
    if (to === targetId) {
      return message([span(isNew ? `* ${getCharId(actorId)} introduces themselves as ${name}.` : `* ${name} introduces themselves again.`, COLORS.me)]);
    }
    return message([span(`* ${label(to, actorId)} introduces themselves to ${label(to, targetId)}.`, COLORS.me)]);
  });
};

const introduceToAll = (actorId) => {
  const name = getName(actorId);
  const near = getOnlineActors().filter((id) => id !== actorId && isInRange(actorId, id, RANGES.say));
  const learned = new Set(near.filter((id) => learn(id, actorId)));
  console.log(`[identity] ${name} [${getCharId(actorId)}] introduced themselves to everyone near (${learned.size} new)`);
  deliverNear(actorId, RANGES.say, (to) => {
    if (to === actorId) {
      return message([span(near.length
        ? `* You introduce yourself to everyone around as ${name}.`
        : `* You introduce yourself as ${name}, but nobody is near enough to hear.`, COLORS.me)]);
    }
    return message([span(learned.has(to)
      ? `* ${getCharId(actorId)} introduces themselves to everyone as ${name}.`
      : `* ${name} introduces themselves to everyone.`, COLORS.me)]);
  });
};

const toggleNames = (actorId) => {
  const ui = mp.get(actorId, 'rpUi') || {};
  const namesHidden = !ui.namesHidden;
  mp.set(actorId, 'rpUi', { ...ui, namesHidden });
  system(actorId, namesHidden ? 'Names above heads hidden. F1 or /names shows them again.' : 'Names above heads shown.');
};

// Runs in the game: draws labels over other players' heads in the game's HUD
// (Scaleform), so they're in screenshots, in the HUD's own font with a dark outline.
//
// Text fields made from outside can't be given a font (their html and embedFonts
// can't be set through SKSE's UI functions), so each line is a copy of the HUD's
// "Letter" symbol (hudmenu.swf): a text field already in $EverywhereMediumFont,
// made with MovieClip.attachMovie. Only writes reach these copies (reading them
// back through SKSE returns nothing), so their state is tracked here.
// Per label slot: two lines (name, then the ID under it once known), each drawn
// four times in black around a light copy.
const NAMES_HUD_SRC = `
  const HUD = 'HUD Menu';
  // State shared by this gamemode's code in the game (labels, players seen, known
  // names). Not sp.storage: it hands back copies, so arrays and changes made inside
  // stored objects don't survive.
  const shared = () => (globalThis.numidiumShared = globalThis.numidiumShared || {});
  const OUTLINE = [[-1.2, 0], [1.2, 0], [0, -1.2], [0, 1.2]];
  const COPIES = OUTLINE.length + 1; // the last copy is the light one
  const LINES = 2;
  const SYMBOL = 'Letter';
  const SYMBOL_FIELD = 'LetterTextInstance';
  const SYMBOL_FONT_SIZE = 55; // the Letter field's own size, scaled down per line
  const SYMBOL_CENTER = 30; // half the Letter field's width: autoSize grows it around this
  const LINE_SIZE = [22, 15];
  const LINE_COLOR = [0xFAFAFA, 0xC8C8C8];
  const MAX_DISTANCE = 640; // about 9 m, measured in game: names show up to this far
  const CONTAINER = 'numidiumNames';
  const ROOT = '_root.' + CONTAINER;
  const clipName = (slot, line, k) => 'rpName' + slot + '_' + line + '_' + k;
  // The HUD's visible area in its own coordinates (it's letterboxed at other aspect ratios)
  const stageRect = (sp, st) => {
    const now = Date.now();
    if (st.rpStage && now < st.rpStage.until) return st.rpStage;
    const get = (p, fallback) => { const v = sp.Ui.getFloat(HUD, 'Stage.visibleRect.' + p); return typeof v === 'number' && isFinite(v) && (p === 'x' || p === 'y' || v > 0) ? v : fallback; };
    st.rpStage = { x: get('x', 0), y: get('y', 0), w: get('width', 1280), h: get('height', 720), until: now + 5000 };
    return st.rpStage;
  };
  // Makes our container and the slot's copies, once per game session
  const ensureClips = (sp, st, slot) => {
    st.rpMade = st.rpMade || {};
    if (!st.rpMade.container) {
      sp.Ui.invokeStringA(HUD, '_root.createEmptyMovieClip', [CONTAINER, '-16200']);
      st.rpMade.container = true;
    }
    if (st.rpMade[slot]) return;
    st.rpMade[slot] = true;
    for (let line = 0; line < LINES; line++) {
      for (let k = 0; k < COPIES; k++) {
        const name = clipName(slot, line, k);
        const clip = ROOT + '.' + name;
        sp.Ui.invokeStringA(HUD, ROOT + '.attachMovie', [SYMBOL, name, String(1 + (slot * LINES + line) * COPIES + k)]);
        const scale = 100 * LINE_SIZE[line] / SYMBOL_FONT_SIZE;
        sp.Ui.setFloat(HUD, clip + '._xscale', scale);
        sp.Ui.setFloat(HUD, clip + '._yscale', scale);
        sp.Ui.setString(HUD, clip + '.' + SYMBOL_FIELD + '.autoSize', 'center');
        sp.Ui.setFloat(HUD, clip + '.' + SYMBOL_FIELD + '.textColor', k < OUTLINE.length ? 0x000000 : LINE_COLOR[line]);
        sp.Ui.setBool(HUD, clip + '._visible', false);
        sp.Ui.setFloat(HUD, clip + '._visible', 0);
      }
    }
    st.rpSlotText = st.rpSlotText || {};
    st.rpSlotText[slot] = [null, null];
    st.rpSlotVisible = st.rpSlotVisible || {};
    st.rpSlotVisible[slot] = [false, false];
  };
  const setLineVisible = (sp, st, slot, line, visible) => {
    if (st.rpSlotVisible[slot][line] === visible) return;
    st.rpSlotVisible[slot][line] = visible;
    for (let k = 0; k < COPIES; k++) {
      // Both ways: setBool alone didn't take on the earlier text fields, a number did
      sp.Ui.setBool(HUD, ROOT + '.' + clipName(slot, line, k) + '._visible', visible);
      sp.Ui.setFloat(HUD, ROOT + '.' + clipName(slot, line, k) + '._visible', visible ? 1 : 0);
    }
  };
  const setVisible = (sp, st, slot, visible) => {
    if (!st.rpSlotVisible || !st.rpSlotVisible[slot]) return;
    for (let line = 0; line < LINES; line++) setLineVisible(sp, st, slot, line, visible && st.rpSlotText[slot][line] !== '');
  };
  const setLineText = (sp, st, slot, line, text) => {
    if (st.rpSlotText[slot][line] === text) return;
    st.rpSlotText[slot][line] = text;
    for (let k = 0; k < COPIES; k++) sp.Ui.setString(HUD, ROOT + '.' + clipName(slot, line, k) + '.' + SYMBOL_FIELD + '.text', text);
  };
  // Puts a line's copies with its center at (x, y)
  const placeLine = (sp, slot, line, x, y, alpha) => {
    const scale = LINE_SIZE[line] / SYMBOL_FONT_SIZE;
    for (let k = 0; k < COPIES; k++) {
      const clip = ROOT + '.' + clipName(slot, line, k);
      const [dx, dy] = k < OUTLINE.length ? OUTLINE[k] : [0, 0];
      sp.Ui.setFloat(HUD, clip + '._x', x - SYMBOL_CENTER * scale + dx);
      sp.Ui.setFloat(HUD, clip + '._y', y - SYMBOL_FONT_SIZE * scale / 2 + dy);
      sp.Ui.setFloat(HUD, clip + '._alpha', k < OUTLINE.length ? alpha * 0.85 : alpha);
    }
  };
  // Problems found in the game, sent to the server's log by the input event source
  const report = (st, text) => {
    st.rpReports = st.rpReports || [];
    st.rpReportKeys = st.rpReportKeys || {};
    const key = String(text).replace(/[0-9]+/g, '#'); // the same problem with other numbers is no news
    if (st.rpReportKeys[key] || st.rpReports.length >= 20) return;
    st.rpReportKeys[key] = true;
    st.rpReports.push(String(text).slice(0, 800));
  };
  const slotFor = (st, serverId) => {
    st.rpSlots = st.rpSlots || {};
    if (st.rpSlots[serverId] !== undefined) return st.rpSlots[serverId];
    const used = new Set(Object.values(st.rpSlots));
    let slot = 0;
    while (used.has(slot)) slot++;
    st.rpSlots[serverId] = slot;
    return slot;
  };
`;

// Other players: their label over the head and in the "Talk ..." prompt, as this
// player knows them. Runs every frame for each player near.
const NAMES_UPDATE_NEIGHBOR = NAMES_HUD_SRC + `
  const tag = ctx.value;
  const sp = ctx.sp;
  const st = shared();
  let step = 'start';
  try {
  if (!tag || typeof tag.id !== 'string' || !ctx.refr) return;
  // The reference again from its id: the one SkyMP hands over can be from an older call
  step = 'refr';
  const formId = ctx.refr.getFormID();
  const refr = sp.ObjectReference.from(sp.Game.getFormEx(formId));
  if (!refr) return;
  const serverId = ctx.getFormIdInServerFormat(formId);
  const name = (st.rpKnown || {})[serverId] || null;
  const text = name || tag.id;
  st.rpSeen = st.rpSeen || {};
  if (!st.rpSeen[serverId]) report(st, 'names: running for ' + tag.id + ' (' + serverId + ')');
  st.rpSeen[serverId] = { at: Date.now(), id: tag.id };
  step = 'displayName';
  if (refr.getDisplayName() !== text) refr.setDisplayName(text, true);

  const slot = slotFor(st, serverId);
  step = 'clips';
  ensureClips(sp, st, slot);
  step = 'player';
  const player = sp.Game.getPlayer();
  const actor = sp.Actor.from(refr);
  step = 'distance';
  const distance = player.getDistance(refr);
  let point = null;
  step = 'visibility';
  if (!st.rpNamesHidden && refr.is3DLoaded() && distance <= MAX_DISTANCE && !(actor && actor.isSneaking()) && player.hasLOS(refr)) {
    step = 'head';
    const head = 'NPC Head [Head]';
    const screen = sp.worldPointToScreenPoint([
      sp.NetImmerse.getNodeWorldPositionX(refr, head, false),
      sp.NetImmerse.getNodeWorldPositionY(refr, head, false),
      sp.NetImmerse.getNodeWorldPositionZ(refr, head, false) + 26,
    ])[0];
    if (screen && screen[2] > 0) point = screen;
  }
  step = 'draw';
  if (!point) {
    setVisible(sp, st, slot, false);
    return;
  }
  // Name over the ID once known, else the ID alone
  setLineText(sp, st, slot, 0, name || tag.id);
  setLineText(sp, st, slot, 1, name ? tag.id : '');
  const rect = stageRect(sp, st);
  const x = rect.x + point[0] * rect.w;
  const bottom = rect.y + (1 - point[1]) * rect.h;
  const alpha = Math.round(100 * Math.min(1, Math.max(0.5, 1 - (distance / MAX_DISTANCE - 0.7) / 0.6)));
  if (name) {
    placeLine(sp, slot, 1, x, bottom - LINE_SIZE[1] * 0.6, alpha);
    placeLine(sp, slot, 0, x, bottom - LINE_SIZE[1] * 1.2 - LINE_SIZE[0] * 0.6, alpha);
  } else {
    placeLine(sp, slot, 0, x, bottom - LINE_SIZE[0] * 0.6, alpha);
  }
  setVisible(sp, st, slot, true);
  if (!st.rpDiagSent) {
    st.rpDiagSent = true;
    report(st, 'names: first label drawn ' + JSON.stringify({ stage: rect, screen: point, x, bottom }));
  }
  } catch (e) {
    report(st, 'names: at ' + step + ': ' + (e && e.stack || e));
  }
`;

const KNOWN_UPDATE_OWNER = `
  const st = globalThis.numidiumShared = globalThis.numidiumShared || {};
  st.rpKnown = ctx.value && typeof ctx.value === 'object' ? ctx.value : {};
`;

const UI_UPDATE_OWNER = `
  const st = globalThis.numidiumShared = globalThis.numidiumShared || {};
  st.rpNamesHidden = !!(ctx.value && ctx.value.namesHidden);
`;

// Runs in the browser: the interaction menu for a player under the crosshair.
// Built from a list of actions, so other targets (doors, chests) can bring theirs.
function browserInteractMenu(data) {
  var sp = window.skyrimPlatform;
  var old = document.getElementById('rp-interact');
  if (old) old.remove();
  if (!data) return;
  var menu = document.createElement('div');
  menu.id = 'rp-interact';
  // Sizes in vh, so the menu scales with the screen like the chat does
  menu.style.cssText = 'position:fixed;left:50%;top:55%;transform:translateX(-50%);min-width:32vh;padding:1.3vh 1.6vh;' +
    'background:rgba(12,12,14,0.85);border:1px solid rgba(255,255,255,0.2);border-radius:0.6vh;z-index:99999;' +
    "font-family:'Segoe UI',Tahoma,Arial,sans-serif;font-weight:600;color:#fafafa;text-shadow:0 0 0.4vh #000;";
  var title = document.createElement('div');
  title.textContent = data.title;
  title.style.cssText = 'font-size:2.4vh;text-align:center;margin-bottom:1vh;letter-spacing:0.05vh;';
  menu.appendChild(title);
  var pick = function (key) { sp.sendMessage('rpinteract:pick', key); };
  data.actions.forEach(function (action) {
    var button = document.createElement('div');
    button.textContent = action.text;
    button.style.cssText = 'font-size:1.85vh;padding:0.8vh 1.2vh;margin:0.5vh 0;cursor:pointer;border:1px solid rgba(255,255,255,0.12);' +
      'background:rgba(255,255,255,0.04);text-align:center;';
    button.onmouseenter = function () { button.style.background = 'rgba(255,255,255,0.16)'; };
    button.onmouseleave = function () { button.style.background = 'rgba(255,255,255,0.04)'; };
    button.onclick = function () { pick(action.key); };
    menu.appendChild(button);
  });
  var hint = document.createElement('div');
  hint.textContent = 'Esc to close';
  hint.style.cssText = 'font-size:1.35vh;color:#a8a8a8;text-align:center;margin-top:0.8vh;';
  menu.appendChild(hint);
  document.body.appendChild(menu);
  var onKey = function (e) {
    if (e.key === 'Escape') {
      document.removeEventListener('keydown', onKey);
      sp.sendMessage('rpinteract:close');
    }
  };
  document.addEventListener('keydown', onKey);
}

const INTERACT_MENU_SRC = '(' + browserInteractMenu.toString() + ')';

const PLAYER_ACTIONS = [
  { key: 'introduce', text: 'Introduce yourself' },
  { key: 'introduceAll', text: 'Introduce yourself to everyone nearby' },
];

// Runs once per client: F1 toggles names; X on another player opens the
// interaction menu; labels of players no longer near are hidden.
const INPUT_EVENT_SOURCE = `try {` + NAMES_HUD_SRC + `
  const KEY_F1 = 0x3B;
  const KEY_X = 0x2D;
  const MENU_SRC = ${JSON.stringify(INTERACT_MENU_SRC)};
  const ACTIONS = ${JSON.stringify(PLAYER_ACTIONS)};
  const sp = ctx.sp;
  const st = shared();
  const down = {};
  let menuTarget = null;
  const pressed = (key) => {
    const isDown = sp.Input.isKeyPressed(key);
    const was = down[key];
    down[key] = isDown;
    return isDown && !was;
  };
  const closeMenu = () => {
    menuTarget = null;
    sp.browser.executeJavaScript(MENU_SRC + '(null)');
    sp.browser.setFocused(false);
    sp.browser.executeJavaScript("window.dispatchEvent(new Event('skymp5-client:browserUnfocused'))");
  };
  sp.on('browserMessage', (e) => {
    const key = e.arguments[0];
    if (key === 'rpinteract:pick' && menuTarget !== null) {
      ctx.sendEvent({ type: 'interact', action: e.arguments[1], target: menuTarget });
      closeMenu();
    } else if (key === 'rpinteract:close') {
      closeMenu();
    }
  });
  let reportAt = 0;
  let xReports = 0;
  sp.on('update', () => {
    const now = Date.now();
    try {
      if (now >= reportAt && st.rpReports && st.rpReports.length) {
        reportAt = now + 5000;
        ctx.sendEvent({ type: 'report', lines: st.rpReports.splice(0, 5) });
      }
      // Labels of players who left (their neighbor update stopped running)
      for (const serverId of Object.keys(st.rpSlots || {})) {
        const seen = (st.rpSeen || {})[serverId];
        if (!seen || now - seen.at > 300) setVisible(sp, st, st.rpSlots[serverId], false);
      }
      const typing = sp.browser.isFocused() || sp.Utility.isInMenuMode();
      if (pressed(KEY_F1) && !typing) {
        st.rpNamesHidden = !st.rpNamesHidden;
        ctx.sendEvent({ type: 'toggleNames' });
      }
      if (pressed(KEY_X) && !typing && menuTarget === null) {
        const ref = sp.Game.getCurrentCrosshairRef();
        const serverId = ref ? ctx.getFormIdInServerFormat(ref.getFormID()) : null;
        const seen = serverId !== null ? (st.rpSeen || {})[serverId] : null;
        if (xReports < 5) {
          xReports++;
          report(st, 'x: ' + JSON.stringify({ ref: ref ? ref.getFormID().toString(16) : null, serverId,
            seen: seen ? now - seen.at : null, seenIds: Object.keys(st.rpSeen || {}) }));
        }
        if (!seen || now - seen.at > 1000) return; // not a player
        menuTarget = serverId;
        const name = (st.rpKnown || {})[serverId];
        sp.browser.setFocused(true);
        sp.browser.executeJavaScript(MENU_SRC + '(' + JSON.stringify({ title: name ? name + ' (' + seen.id + ')' : seen.id, actions: ACTIONS }) + ')');
      }
    } catch (e) {
      report(st, 'input: ' + (e && e.stack || e));
    }
  });
  ctx.sendEvent({ type: 'report', lines: ['input: started'] });
} catch (e) {
  ctx.sendEvent({ type: 'report', lines: ['input: failed to start: ' + (e && e.stack || e)] });
}
`;

// Visible by the owner too: SkyMP never sends neighbors a property its owner can't see
mp.makeProperty('rpTag', {
  isVisibleByOwner: true,
  isVisibleByNeighbors: true,
  updateOwner: '',
  updateNeighbor: NAMES_UPDATE_NEIGHBOR,
});

mp.makeProperty('rpKnown', {
  isVisibleByOwner: true,
  isVisibleByNeighbors: false,
  updateOwner: KNOWN_UPDATE_OWNER,
  updateNeighbor: '',
});

mp.makeProperty('rpUi', {
  isVisibleByOwner: true,
  isVisibleByNeighbors: false,
  updateOwner: UI_UPDATE_OWNER,
  updateNeighbor: '',
});

mp.makeEventSource('_rpInput', INPUT_EVENT_SOURCE);

mp._rpInput = (actorId, data) => {
  try {
    if (!data || typeof data !== 'object') return;
    if (data.type === 'report' && Array.isArray(data.lines)) {
      for (const line of data.lines.slice(0, 5)) console.log(`[client] ${getName(actorId)} [${getCharId(actorId)}]: ${String(line).slice(0, 800)}`);
    } else if (data.type === 'toggleNames') {
      const ui = mp.get(actorId, 'rpUi') || {};
      mp.set(actorId, 'rpUi', { ...ui, namesHidden: !ui.namesHidden });
    } else if (data.type === 'interact') {
      if (data.action === 'introduce' && typeof data.target === 'number') introduce(actorId, data.target);
      else if (data.action === 'introduceAll') introduceToAll(actorId);
    }
  } catch (e) {
    console.error('[identity] failed to handle input', e);
  }
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
// carrying your synced body, to see what other players see without a second game.
//
// One double per player, shown and hidden rather than created and destroyed:
// destroyActor only disables an actor and keeps it in the save, and its id
// lingers in the profile index. The double's id is kept on the player
// (rpBodyDouble), so it survives gamemode reloads and restarts. Doubles carry a
// profile id no player has, so ones left showing can be hidden on load.
const BODY_DOUBLE_PROFILE_ID = 0x7ffffff0;

const hideLeftoverBodyDoubles = () => {
  let hidden = 0;
  for (const id of mp.getActorsByProfileId(BODY_DOUBLE_PROFILE_ID)) {
    if (mp.get(id, 'isDisabled')) continue;
    mp.set(id, 'isDisabled', true);
    hidden++;
  }
  if (hidden) console.log(`[body] hid ${hidden} body double(s) left showing`);
};

// Dev commands (/double, /racemenu) and the game console: for everyone on a dev
// server (enableConsoleCommandsForAll), otherwise for the Discord ids listed in
// numidiumAuth.admins
const isAdmin = (actorId) => {
  const admins = (mp.getServerSettings().numidiumAuth || {}).admins;
  const discordId = mp.get(actorId, 'private.indexed.discordId');
  return Array.isArray(admins) && typeof discordId === 'string' && admins.includes(discordId);
};

const devCommandsEnabled = (actorId) => mp.getServerSettings().enableConsoleCommandsForAll === true || isAdmin(actorId);

const toggleBodyDouble = (actorId) => {
  let doubleId = mp.get(actorId, 'rpBodyDouble');
  if (typeof doubleId === 'number' && !mp.get(doubleId, 'isDisabled')) {
    mp.set(doubleId, 'isDisabled', true);
    system(actorId, 'Body double hidden.');
    return;
  }
  const [x, y, z] = mp.getActorPos(actorId);
  const angle = mp.get(actorId, 'angle');
  const angleZ = Array.isArray(angle) ? angle[2] : 0;
  const rad = (angleZ * Math.PI) / 180;
  const pos = [x + Math.sin(rad) * 150, y + Math.cos(rad) * 150, z];
  if (typeof doubleId !== 'number') {
    doubleId = mp.createActor(0, pos, angleZ + 180, mp.getActorCellOrWorld(actorId), BODY_DOUBLE_PROFILE_ID);
    mp.set(actorId, 'rpBodyDouble', doubleId);
  }
  mp.set(doubleId, 'appearance', mp.get(actorId, 'appearance'));
  const body = mp.get(actorId, 'rpBody');
  if (body) mp.set(doubleId, 'rpBody', body);
  // Moving it in front of the player also puts a new double on the streaming
  // grid, which createActor doesn't; a hidden one starts streaming when shown
  const { cellOrWorldDesc } = mp.get(actorId, 'locationalData');
  mp.set(doubleId, 'locationalData', { cellOrWorldDesc, pos, rot: [0, 0, angleZ + 180] });
  mp.set(doubleId, 'isDisabled', false);
  system(actorId, body
    ? `Body double shown with your synced body (${body.morphs.length} morphs, ${body.scales.length} bones). /double again hides it.`
    : 'Body double shown, but you have no synced body yet: open and close RaceMenu first.');
};

// ---------------------------------------------------------------------------
// Factions: the Jarldoms, their Guards, and whatever admins create later
// ---------------------------------------------------------------------------
//
// Kept in numidium-factions.json in the server folder rather than on actors, so
// leaders can manage members who are offline. Members are characters, by ID.
//
//   factions: { [factionId]: { name, seat?, parent, group, ranks } }
//     ranks: [{ id, name, perms, actsAs? }], from the top rank down
//     group: a character is in at most one faction of a group ('hold', 'guard');
//       null for no limit
//     actsAs: { group, rank }: in the sub-faction of that group, members of this
//       rank count as that rank for permissions (a hold's housecarls are
//       guardsmen of its Guard), without being listed as its members
//   members: { [charId]: { [factionId]: rankId } }
//   invites: { [charId]: { [factionId]: { rank, by, at } } }, accepted with
//     /faction accept; they expire after INVITE_DAYS
//
// Permissions (a rank's perms):
//   manage   everything in the faction and its sub-factions: members, ranks,
//            names (the Jarl). The parent's leader manages the sub-faction too
//   recruit  invite people, to ranks below one's own
//   dismiss  remove members ranked below one's own
//   promote  change the rank of members below one's own, to ranks below it
//   property grant house and storage rights (not built yet)
//   cuff     cuff people (not built yet)
// Only people ranked below you can be acted on, and only ranks below yours given.
// Admins (numidiumAuth.admins) rank above everyone, in every faction.

const fs = require('fs');
const path = require('path');

const FACTIONS_FILE = path.resolve('numidium-factions.json');
const INVITE_DAYS = 3;
const FACTION_ID_PATTERN = /^[a-z][a-z0-9-]{1,31}$/;
const RANK_ID_PATTERN = /^[a-z][a-z0-9-]{0,23}$/;
const MAX_FACTION_NAME_LENGTH = 48;
const PERMISSIONS = ['manage', 'recruit', 'dismiss', 'promote', 'property', 'cuff'];

const FACTION_TEMPLATES = {
  hold: {
    group: 'hold',
    ranks: [
      { id: 'jarl', name: 'Jarl', perms: ['manage'] },
      { id: 'steward', name: 'Steward', perms: ['property'] },
      { id: 'housecarl', name: 'Housecarl', perms: [], actsAs: { group: 'guard', rank: 'guardsman' } },
      { id: 'court', name: 'Court', perms: [] },
    ],
  },
  guard: {
    group: 'guard',
    ranks: [
      { id: 'commander', name: 'Commander', perms: ['recruit', 'dismiss', 'promote', 'cuff'] },
      { id: 'captain', name: 'Captain', perms: ['recruit', 'dismiss', 'promote', 'cuff'] },
      { id: 'lieutenant', name: 'Lieutenant', perms: ['recruit', 'dismiss', 'promote', 'cuff'] },
      { id: 'sergeant', name: 'Sergeant', perms: ['cuff'] },
      { id: 'guardsman', name: 'Guardsman', perms: ['cuff'] },
      { id: 'recruit', name: 'Recruit', perms: [] },
    ],
  },
  // A plain faction for anything else: a leader, members, no limits
  basic: {
    group: null,
    ranks: [
      { id: 'leader', name: 'Leader', perms: ['manage'] },
      { id: 'member', name: 'Member', perms: [] },
    ],
  },
};

// The nine Jarldoms (id, name, seat), each with its Guard (<id>-guard)
const HOLDS = [
  ['haafingar', 'Haafingar', 'Solitude'],
  ['hjaalmarch', 'Hjaalmarch', 'Morthal'],
  ['reach', 'The Reach', 'Markarth'],
  ['falkreath', 'Falkreath', 'Falkreath'],
  ['whiterun', 'Whiterun', 'Whiterun'],
  ['pale', 'The Pale', 'Dawnstar'],
  ['rift', 'The Rift', 'Riften'],
  ['winterhold', 'Winterhold', 'Winterhold'],
  ['eastmarch', 'Eastmarch', 'Windhelm'],
];

const copyRanks = (ranks) => ranks.map((rank) => ({ ...rank, perms: [...rank.perms], ...(rank.actsAs ? { actsAs: { ...rank.actsAs } } : {}) }));

const newFaction = (template, name, parent, seat) => ({
  name,
  ...(seat ? { seat } : {}),
  parent: parent || null,
  group: FACTION_TEMPLATES[template].group,
  ranks: copyRanks(FACTION_TEMPLATES[template].ranks),
});

const defaultFactions = () => {
  const factions = {};
  for (const [id, name, seat] of HOLDS) {
    factions[id] = newFaction('hold', name, null, seat);
    factions[`${id}-guard`] = newFaction('guard', `${seat} Guard`, id);
  }
  return { factions, members: {}, invites: {} };
};

const loadFactions = () => {
  if (!fs.existsSync(FACTIONS_FILE)) {
    const data = defaultFactions();
    saveFactionsTo(data);
    console.log(`[faction] created ${FACTIONS_FILE} with the ${HOLDS.length} Jarldoms and their Guards`);
    return data;
  }
  const data = JSON.parse(fs.readFileSync(FACTIONS_FILE, 'utf8'));
  return { factions: data.factions || {}, members: data.members || {}, invites: data.invites || {} };
};

function saveFactionsTo(data) {
  const temp = FACTIONS_FILE + '.tmp';
  fs.writeFileSync(temp, JSON.stringify(data, null, 2));
  fs.renameSync(temp, FACTIONS_FILE);
}

// Loaded once per gamemode (re)load; every change is written straight back
let factionData;
try {
  factionData = loadFactions();
} catch (e) {
  // A broken file mustn't be overwritten with an empty one: factions stay off
  console.error(`[faction] couldn't read ${FACTIONS_FILE}; factions are off until it's fixed`, e);
  factionData = null;
}

const saveFactions = () => saveFactionsTo(factionData);

const factionLog = (text) => console.log(`[faction] ${text}`);

const findFaction = (text) => {
  if (!text) return null;
  const key = text.toLowerCase();
  if (factionData.factions[key]) return key;
  return Object.keys(factionData.factions).find((id) => factionData.factions[id].name.toLowerCase() === key) || null;
};

const rankIndex = (factionId, rankText) => {
  if (!rankText) return -1;
  const key = rankText.toLowerCase();
  return factionData.factions[factionId].ranks.findIndex((r) => r.id === key || r.name.toLowerCase() === key);
};

const rankName = (factionId, rankId) => {
  const rank = factionData.factions[factionId].ranks.find((r) => r.id === rankId);
  return rank ? rank.name : rankId;
};

const subFactions = (factionId) => Object.keys(factionData.factions).filter((id) => factionData.factions[id].parent === factionId);

// The faction above factionId and the one above that, and so on
const ancestors = (factionId) => {
  const chain = [];
  let parent = factionData.factions[factionId].parent;
  while (parent && factionData.factions[parent] && !chain.includes(parent)) {
    chain.push(parent);
    parent = factionData.factions[parent].parent;
  }
  return chain;
};

const membershipsOf = (charId) => factionData.members[charId] || {};

const ADMIN_LEVEL = -2;
const PARENT_LEADER_LEVEL = -1;
const NO_LEVEL = Infinity;

// How high charId stands in factionId: a rank index (0 is the top rank), or
// PARENT_LEADER_LEVEL for someone who manages a faction above it; with the
// permissions that come with it
const standing = (charId, factionId) => {
  const faction = factionData.factions[factionId];
  let level = NO_LEVEL;
  let perms = new Set();
  const take = (index) => {
    if (index < 0 || index >= level) return;
    level = index;
    perms = new Set(faction.ranks[index].perms);
  };
  const own = membershipsOf(charId)[factionId];
  if (own !== undefined) take(faction.ranks.findIndex((r) => r.id === own));
  for (const ancestorId of ancestors(factionId)) {
    const ancestorRankId = membershipsOf(charId)[ancestorId];
    if (ancestorRankId === undefined) continue;
    const ancestorRank = factionData.factions[ancestorId].ranks.find((r) => r.id === ancestorRankId);
    if (!ancestorRank) continue;
    if (ancestorRank.perms.includes('manage')) {
      return { level: PARENT_LEADER_LEVEL, perms: new Set(PERMISSIONS) };
    }
    // A hold's housecarls are guardsmen of its own Guard
    if (ancestorRank.actsAs && ancestorRank.actsAs.group === faction.group && ancestorId === faction.parent) {
      take(faction.ranks.findIndex((r) => r.id === ancestorRank.actsAs.rank));
    }
  }
  if (perms.has('manage')) perms = new Set(PERMISSIONS);
  return { level, perms };
};

const actorStanding = (actorId, factionId) => {
  if (isAdmin(actorId)) return { level: ADMIN_LEVEL, perms: new Set(PERMISSIONS) };
  const charId = getCharId(actorId);
  return charId ? standing(charId, factionId) : { level: NO_LEVEL, perms: new Set() };
};

// Whether actorId has perm in factionId: for other systems (cuffs, property)
const factionCan = (actorId, factionId, perm) => factionData !== null && !!factionData.factions[factionId]
  && actorStanding(actorId, factionId).perms.has(perm);

// The actor form of a character ID, or null
const actorOfCharId = (charId) => {
  const forms = mp.findFormsByPropertyValue('private.indexed.rpCharId', charId);
  return forms.length ? forms[0] : null;
};

// How viewerId sees a character known by ID: name and ID if they know them
const describeChar = (viewerId, charId) => {
  const actorId = actorOfCharId(charId);
  if (actorId === null) return charId;
  return knows(viewerId, actorId) ? `${getName(actorId)} [${charId}]` : charId;
};

const tellChar = (charId, text) => {
  const actorId = actorOfCharId(charId);
  if (actorId !== null && isOnline(actorId)) system(actorId, text);
};

const setMembership = (charId, factionId, rankId) => {
  const memberships = { ...membershipsOf(charId) };
  if (rankId === null) delete memberships[factionId];
  else memberships[factionId] = rankId;
  if (Object.keys(memberships).length) factionData.members[charId] = memberships;
  else delete factionData.members[charId];
};

const dropInvite = (charId, factionId) => {
  const invites = { ...(factionData.invites[charId] || {}) };
  delete invites[factionId];
  if (Object.keys(invites).length) factionData.invites[charId] = invites;
  else delete factionData.invites[charId];
};

const pendingInvites = (charId) => {
  const now = Date.now();
  const invites = factionData.invites[charId] || {};
  return Object.keys(invites).filter((factionId) => factionData.factions[factionId]
    && now - invites[factionId].at < INVITE_DAYS * 24 * 3600 * 1000);
};

// The faction of the same group that charId is already in, if any
const sameGroupFaction = (charId, factionId) => {
  const group = factionData.factions[factionId].group;
  if (!group) return null;
  return Object.keys(membershipsOf(charId)).find((id) => id !== factionId
    && factionData.factions[id] && factionData.factions[id].group === group) || null;
};

// "/f <command> <faction> <ID> ..." helpers: each replies to the actor and
// returns null when something's wrong
const needFaction = (actorId, text) => {
  const factionId = findFaction(text);
  if (!factionId) system(actorId, text ? `There's no faction "${text}". /faction all lists them.` : 'Which faction? /faction all lists them.');
  return factionId;
};

const needCharId = (actorId, text) => {
  const charId = (text || '').toUpperCase();
  if (!charId || actorOfCharId(charId) === null) {
    system(actorId, text ? `Nobody has the ID ${charId}.` : 'Whose ID? It\'s the code over their head.');
    return null;
  }
  return charId;
};

const needRank = (actorId, factionId, text, actorLevel) => {
  const index = text ? rankIndex(factionId, text) : factionData.factions[factionId].ranks.length - 1;
  const names = factionData.factions[factionId].ranks.map((r) => r.name).join(', ');
  if (index < 0) {
    system(actorId, `${factionData.factions[factionId].name} has no rank "${text}". Its ranks: ${names}.`);
    return null;
  }
  if (index <= actorLevel) {
    system(actorId, `You can only give ranks below your own.`);
    return null;
  }
  return index;
};

// Only those ranked below the actor can be acted on
const needBelow = (actorId, factionId, charId, actorLevel) => {
  const rankId = membershipsOf(charId)[factionId];
  if (rankId === undefined) {
    system(actorId, `${describeChar(actorId, charId)} isn't in ${factionData.factions[factionId].name}.`);
    return false;
  }
  if (factionData.factions[factionId].ranks.findIndex((r) => r.id === rankId) <= actorLevel) {
    system(actorId, `${describeChar(actorId, charId)} isn't ranked below you.`);
    return false;
  }
  return true;
};

const needPerm = (actorId, factionId, perm, what) => {
  const { level, perms } = actorStanding(actorId, factionId);
  if (!perms.has(perm)) {
    system(actorId, `You can't ${what} in ${factionData.factions[factionId].name}.`);
    return null;
  }
  return level;
};

const listOwnFactions = (actorId) => {
  const charId = getCharId(actorId);
  const memberships = membershipsOf(charId);
  const ids = Object.keys(memberships).filter((id) => factionData.factions[id]);
  system(actorId, ids.length
    ? `Your factions: ${ids.map((id) => `${factionData.factions[id].name} (${rankName(id, memberships[id])})`).join(', ')}.`
    : 'You are in no faction.');
  for (const factionId of pendingInvites(charId)) {
    const invite = factionData.invites[charId][factionId];
    system(actorId, `Invitation: ${factionData.factions[factionId].name}, as ${rankName(factionId, invite.rank)}. `
      + `/faction accept ${factionId} or /faction decline ${factionId}.`);
  }
};

const listAllFactions = (actorId) => {
  const ids = Object.keys(factionData.factions);
  const line = (id, depth) => {
    const faction = factionData.factions[id];
    system(actorId, `${'  '.repeat(depth)}${faction.name}${faction.seat ? ` (${faction.seat})` : ''}: ${id}`);
    for (const sub of subFactions(id)) line(sub, depth + 1);
  };
  system(actorId, 'Factions (name: id):');
  for (const id of ids.filter((id) => !factionData.factions[id].parent || !factionData.factions[factionData.factions[id].parent])) line(id, 0);
};

const showFaction = (actorId, factionId) => {
  const faction = factionData.factions[factionId];
  const parent = faction.parent && factionData.factions[faction.parent];
  system(actorId, `${faction.name}${faction.seat ? `, seat ${faction.seat}` : ''} (${factionId})`
    + `${parent ? `, part of ${parent.name}` : ''}${faction.group ? `, one per character among the ${faction.group} factions` : ''}.`);
  const counts = {};
  for (const charId of Object.keys(factionData.members)) {
    const rankId = factionData.members[charId][factionId];
    if (rankId !== undefined) (counts[rankId] = counts[rankId] || []).push(charId);
  }
  // Members see who's in it; others only how many
  const { level } = actorStanding(actorId, factionId);
  const seesMembers = level !== NO_LEVEL;
  for (const rank of faction.ranks) {
    const members = counts[rank.id] || [];
    const perms = rank.perms.length ? ` [${rank.perms.join(', ')}]` : '';
    const acts = rank.actsAs ? ` [counts as ${rank.actsAs.rank} in the ${rank.actsAs.group}]` : '';
    const who = seesMembers && members.length ? `: ${members.map((c) => describeChar(actorId, c)).join(', ')}` : '';
    system(actorId, `  ${rank.name} (${rank.id})${perms}${acts}: ${members.length}${who}`);
  }
};

const invite = (actorId, args) => {
  const [factionText, charText, rankText] = args;
  const factionId = needFaction(actorId, factionText);
  if (!factionId) return;
  const level = needPerm(actorId, factionId, 'recruit', 'invite people');
  if (level === null) return;
  const charId = needCharId(actorId, charText);
  if (!charId) return;
  const faction = factionData.factions[factionId];
  if (membershipsOf(charId)[factionId] !== undefined) {
    system(actorId, `${describeChar(actorId, charId)} is already in ${faction.name}. /faction rank changes their rank.`);
    return;
  }
  const index = needRank(actorId, factionId, rankText, level);
  if (index === null) return;
  const rankId = faction.ranks[index].id;
  factionData.invites[charId] = { ...(factionData.invites[charId] || {}), [factionId]: { rank: rankId, by: getCharId(actorId), at: Date.now() } };
  saveFactions();
  factionLog(`${getName(actorId)} [${getCharId(actorId)}] invited ${charId} to ${factionId} as ${rankId}`);
  system(actorId, `You invited ${describeChar(actorId, charId)} to ${faction.name} as ${faction.ranks[index].name}. `
    + `They have ${INVITE_DAYS} days to accept.`);
  tellChar(charId, `You're invited to join ${faction.name} as ${faction.ranks[index].name}. `
    + `/faction accept ${factionId} or /faction decline ${factionId}.`);
};

const respondToInvite = (actorId, args, accept) => {
  const charId = getCharId(actorId);
  const pending = pendingInvites(charId);
  const factionId = args[0] ? findFaction(args[0]) : pending.length === 1 ? pending[0] : null;
  if (!factionId || !pending.includes(factionId)) {
    system(actorId, pending.length
      ? `Which invitation? You have: ${pending.join(', ')}.`
      : 'You have no invitation.');
    return;
  }
  const faction = factionData.factions[factionId];
  const { rank, by } = factionData.invites[charId][factionId];
  if (!accept) {
    dropInvite(charId, factionId);
    saveFactions();
    factionLog(`${charId} declined ${factionId}`);
    system(actorId, `You declined to join ${faction.name}.`);
    return;
  }
  const other = sameGroupFaction(charId, factionId);
  if (other) {
    system(actorId, `You're already in ${factionData.factions[other].name}: one ${faction.group} at a time. `
      + `/faction leave ${other} first.`);
    return;
  }
  if (rankIndex(factionId, rank) < 0) {
    dropInvite(charId, factionId);
    saveFactions();
    system(actorId, `That invitation's rank no longer exists in ${faction.name}; ask for a new one.`);
    return;
  }
  dropInvite(charId, factionId);
  setMembership(charId, factionId, rank);
  saveFactions();
  factionLog(`${getName(actorId)} [${charId}] joined ${factionId} as ${rank}`);
  system(actorId, `You joined ${faction.name} as ${rankName(factionId, rank)}.`);
  if (by) {
    const byActor = actorOfCharId(by);
    if (byActor !== null && isOnline(byActor)) system(byActor, `${describeChar(byActor, charId)} joined ${faction.name}.`);
  }
};

const dismiss = (actorId, args) => {
  const [factionText, charText] = args;
  const factionId = needFaction(actorId, factionText);
  if (!factionId) return;
  const level = needPerm(actorId, factionId, 'dismiss', 'remove members');
  if (level === null) return;
  const charId = needCharId(actorId, charText);
  if (!charId || !needBelow(actorId, factionId, charId, level)) return;
  setMembership(charId, factionId, null);
  saveFactions();
  const faction = factionData.factions[factionId];
  factionLog(`${getName(actorId)} [${getCharId(actorId)}] removed ${charId} from ${factionId}`);
  system(actorId, `You removed ${describeChar(actorId, charId)} from ${faction.name}.`);
  tellChar(charId, `You were removed from ${faction.name}.`);
};

const changeRank = (actorId, args) => {
  const [factionText, charText, rankText] = args;
  const factionId = needFaction(actorId, factionText);
  if (!factionId) return;
  const level = needPerm(actorId, factionId, 'promote', 'change ranks');
  if (level === null) return;
  const charId = needCharId(actorId, charText);
  if (!charId || !needBelow(actorId, factionId, charId, level)) return;
  if (!rankText) {
    system(actorId, 'Which rank? /faction rank <faction> <ID> <rank>');
    return;
  }
  const index = needRank(actorId, factionId, rankText, level);
  if (index === null) return;
  const faction = factionData.factions[factionId];
  setMembership(charId, factionId, faction.ranks[index].id);
  saveFactions();
  factionLog(`${getName(actorId)} [${getCharId(actorId)}] set ${charId} to ${faction.ranks[index].id} in ${factionId}`);
  system(actorId, `${describeChar(actorId, charId)} is now ${faction.ranks[index].name} in ${faction.name}.`);
  tellChar(charId, `You are now ${faction.ranks[index].name} in ${faction.name}.`);
};

const leave = (actorId, args) => {
  const charId = getCharId(actorId);
  const factionId = needFaction(actorId, args[0]);
  if (!factionId) return;
  const faction = factionData.factions[factionId];
  if (membershipsOf(charId)[factionId] === undefined) {
    system(actorId, `You aren't in ${faction.name}.`);
    return;
  }
  setMembership(charId, factionId, null);
  saveFactions();
  factionLog(`${getName(actorId)} [${charId}] left ${factionId}`);
  system(actorId, `You left ${faction.name}.`);
};

// Admin commands: shaping factions and appointing people directly

const adminSet = (actorId, args) => {
  const [factionText, charText, rankText] = args;
  const factionId = needFaction(actorId, factionText);
  if (!factionId) return;
  const charId = needCharId(actorId, charText);
  if (!charId) return;
  const faction = factionData.factions[factionId];
  if (rankText && rankText.toLowerCase() === 'none') {
    setMembership(charId, factionId, null);
    saveFactions();
    factionLog(`admin ${getName(actorId)} removed ${charId} from ${factionId}`);
    system(actorId, `${describeChar(actorId, charId)} is no longer in ${faction.name}.`);
    return;
  }
  const index = needRank(actorId, factionId, rankText || faction.ranks[0].id, ADMIN_LEVEL);
  if (index === null) return;
  const other = sameGroupFaction(charId, factionId);
  if (other) setMembership(charId, other, null);
  setMembership(charId, factionId, faction.ranks[index].id);
  dropInvite(charId, factionId);
  saveFactions();
  factionLog(`admin ${getName(actorId)} set ${charId} to ${faction.ranks[index].id} in ${factionId}${other ? ` (out of ${other})` : ''}`);
  system(actorId, `${describeChar(actorId, charId)} is now ${faction.ranks[index].name} in ${faction.name}`
    + `${other ? ` (and no longer in ${factionData.factions[other].name})` : ''}.`);
  tellChar(charId, `You are now ${faction.ranks[index].name} in ${faction.name}.`);
};

const adminCreate = (actorId, args, rest) => {
  const [idText, template] = args;
  const id = (idText || '').toLowerCase();
  const name = rest(2);
  if (!FACTION_ID_PATTERN.test(id) || !FACTION_TEMPLATES[template] || !name) {
    system(actorId, `Usage: /faction create <id> <${Object.keys(FACTION_TEMPLATES).join('|')}> <Name>, `
      + 'for example /faction create companions basic The Companions. Ids: lowercase letters, digits, dashes.');
    return;
  }
  if (factionData.factions[id]) {
    system(actorId, `There's already a faction ${id}.`);
    return;
  }
  factionData.factions[id] = newFaction(template, name.slice(0, MAX_FACTION_NAME_LENGTH), null);
  saveFactions();
  factionLog(`admin ${getName(actorId)} created ${id} (${template}): ${name}`);
  system(actorId, `Created ${name} (${id}) from the ${template} template. /faction parent ${id} <faction> puts it under another.`);
};

const adminDelete = (actorId, args) => {
  const factionId = needFaction(actorId, args[0]);
  if (!factionId) return;
  if (args[1] !== 'confirm') {
    const members = Object.keys(factionData.members).filter((c) => factionData.members[c][factionId] !== undefined).length;
    system(actorId, `This deletes ${factionData.factions[factionId].name} and its ${members} membership(s). `
      + `/faction delete ${factionId} confirm to go ahead.`);
    return;
  }
  const name = factionData.factions[factionId].name;
  delete factionData.factions[factionId];
  for (const charId of Object.keys(factionData.members)) setMembership(charId, factionId, null);
  for (const charId of Object.keys(factionData.invites)) dropInvite(charId, factionId);
  for (const sub of subFactions(factionId)) factionData.factions[sub].parent = null;
  saveFactions();
  factionLog(`admin ${getName(actorId)} deleted ${factionId}`);
  system(actorId, `Deleted ${name}.`);
};

const adminRename = (actorId, args, rest) => {
  const factionId = needFaction(actorId, args[0]);
  if (!factionId) return;
  const name = rest(1);
  if (!name) {
    system(actorId, 'Usage: /faction rename <faction> <New name>');
    return;
  }
  factionData.factions[factionId].name = name.slice(0, MAX_FACTION_NAME_LENGTH);
  saveFactions();
  factionLog(`admin ${getName(actorId)} renamed ${factionId} to ${name}`);
  system(actorId, `${factionId} is now called ${factionData.factions[factionId].name}.`);
};

const adminParent = (actorId, args) => {
  const factionId = needFaction(actorId, args[0]);
  if (!factionId) return;
  if ((args[1] || '').toLowerCase() === 'none') {
    factionData.factions[factionId].parent = null;
  } else {
    const parentId = needFaction(actorId, args[1]);
    if (!parentId) return;
    if (parentId === factionId || ancestors(parentId).includes(factionId)) {
      system(actorId, `${factionData.factions[parentId].name} is inside ${factionData.factions[factionId].name} already.`);
      return;
    }
    factionData.factions[factionId].parent = parentId;
  }
  saveFactions();
  const parent = factionData.factions[factionId].parent;
  factionLog(`admin ${getName(actorId)} set ${factionId}'s parent to ${parent}`);
  system(actorId, parent
    ? `${factionData.factions[factionId].name} is now part of ${factionData.factions[parent].name}.`
    : `${factionData.factions[factionId].name} is now on its own.`);
};

// /faction ranks <faction> add <id> <Name> | remove <rank> | rename <rank> <Name>
//   | perms <rank> +perm -perm ... | move <rank> up|down
const adminRanks = (actorId, args, rest) => {
  const factionId = needFaction(actorId, args[0]);
  if (!factionId) return;
  const faction = factionData.factions[factionId];
  const action = (args[1] || '').toLowerCase();
  const usage = () => system(actorId, `Usage: /faction ranks ${factionId} add <id> <Name> | remove <rank> | rename <rank> <Name> `
    + `| perms <rank> +perm -perm (${PERMISSIONS.join(', ')}) | move <rank> up|down. New ranks go at the bottom.`);
  if (action === 'add') {
    const id = (args[2] || '').toLowerCase();
    const name = rest(3);
    if (!RANK_ID_PATTERN.test(id) || !name) return usage();
    if (faction.ranks.some((r) => r.id === id)) {
      system(actorId, `${faction.name} already has a rank ${id}.`);
      return;
    }
    faction.ranks.push({ id, name: name.slice(0, MAX_FACTION_NAME_LENGTH), perms: [] });
  } else {
    const index = rankIndex(factionId, args[2]);
    if (!['remove', 'rename', 'perms', 'move'].includes(action)) return usage();
    if (index < 0) {
      system(actorId, `${faction.name} has no rank "${args[2] || ''}". Its ranks: ${faction.ranks.map((r) => r.id).join(', ')}.`);
      return;
    }
    const rank = faction.ranks[index];
    if (action === 'remove') {
      const holders = Object.keys(factionData.members).filter((c) => factionData.members[c][factionId] === rank.id);
      if (holders.length) {
        system(actorId, `${holders.length} member(s) hold ${rank.name}: give them another rank first.`);
        return;
      }
      if (faction.ranks.length === 1) {
        system(actorId, 'A faction needs at least one rank.');
        return;
      }
      faction.ranks.splice(index, 1);
    } else if (action === 'rename') {
      const name = rest(3);
      if (!name) return usage();
      rank.name = name.slice(0, MAX_FACTION_NAME_LENGTH);
    } else if (action === 'perms') {
      for (const change of args.slice(3)) {
        const perm = change.slice(1).toLowerCase();
        if (!/^[+-]/.test(change) || !PERMISSIONS.includes(perm)) return usage();
        rank.perms = change[0] === '+' ? [...new Set([...rank.perms, perm])] : rank.perms.filter((p) => p !== perm);
      }
    } else {
      const to = (args[3] || '').toLowerCase() === 'up' ? index - 1 : (args[3] || '').toLowerCase() === 'down' ? index + 1 : -1;
      if (to < 0 || to >= faction.ranks.length) return usage();
      faction.ranks.splice(index, 1);
      faction.ranks.splice(to, 0, rank);
    }
  }
  saveFactions();
  factionLog(`admin ${getName(actorId)} changed ${factionId}'s ranks: ${args.slice(1).join(' ')}`);
  showFaction(actorId, factionId);
};

const FACTION_HELP = [
  'Factions (/faction or /f):',
  '  /f — your factions and invitations',
  '  /f all — every faction;  /f info <faction> — its ranks and members',
  '  /f invite <faction> <ID> [rank] — invite someone (by the ID over their head)',
  '  /f accept [faction]  /f decline [faction] — answer an invitation',
  '  /f remove <faction> <ID>  /f rank <faction> <ID> <rank> — for those ranked below you',
  '  /f leave <faction>',
];

const FACTION_ADMIN_HELP = [
  'Admin: /f set <faction> <ID> [rank|none] — appoint or remove directly (e.g. a Jarl)',
  '  /f create <id> <hold|guard|basic> <Name>  /f parent <faction> <faction|none>',
  '  /f rename <faction> <Name>  /f delete <faction>',
  '  /f ranks <faction> add|remove|rename|perms|move ...',
];

const factionCommand = (actorId, argsText) => {
  if (!factionData) {
    system(actorId, 'Factions are unavailable: the server couldn\'t read its faction file.');
    return;
  }
  if (!getCharId(actorId)) return;
  const words = argsText ? argsText.split(' ') : [];
  const sub = (words[0] || '').toLowerCase();
  const args = words.slice(1);
  // The words from the n-th argument on, as typed (names with spaces)
  const rest = (n) => args.slice(n).join(' ').trim();
  const admin = isAdmin(actorId);
  const adminOnly = (fn) => {
    if (!admin) {
      system(actorId, 'Only admins can do that.');
      return;
    }
    fn(actorId, args, rest);
  };
  switch (sub) {
    case '':
    case 'list': return listOwnFactions(actorId);
    case 'all': return listAllFactions(actorId);
    case 'info': {
      const factionId = needFaction(actorId, args[0]);
      return factionId && showFaction(actorId, factionId);
    }
    case 'invite':
    case 'add': return invite(actorId, args);
    case 'accept': return respondToInvite(actorId, args, true);
    case 'decline': return respondToInvite(actorId, args, false);
    case 'remove':
    case 'kick': return dismiss(actorId, args);
    case 'rank':
    case 'promote':
    case 'demote': return changeRank(actorId, args);
    case 'leave': return leave(actorId, args);
    case 'set': return adminOnly(adminSet);
    case 'create': return adminOnly(adminCreate);
    case 'delete': return adminOnly(adminDelete);
    case 'rename': return adminOnly(adminRename);
    case 'parent': return adminOnly(adminParent);
    case 'ranks': return adminOnly(adminRanks);
    case 'help':
    default:
      for (const line of FACTION_HELP) system(actorId, line);
      if (admin) for (const line of FACTION_ADMIN_HELP) system(actorId, line);
  }
};

// ---------------------------------------------------------------------------
// Teleports (admins): /tp to the holds' capitals, towns and landmarks
// ---------------------------------------------------------------------------
//
// Each place is its fast-travel map marker, as the load order leaves it (the city
// overhauls move some). The walled cities' markers are inside their own
// worldspaces, so /tp whiterun lands within the walls; "-stables"/"-gate" are outside.

const PLACES = [
  // Holds
  { id: 'solitude', name: "Solitude (Haafingar)", group: 'Holds', world: '37edf:Skyrim.esm', pos: [-64943, 104648, -8426], angleZ: 84 },
  { id: 'morthal', name: "Morthal (Hjaalmarch)", group: 'Holds', world: '3c:Skyrim.esm', pos: [-38640, 66734, -13244], angleZ: 85 },
  { id: 'markarth', name: "Markarth (The Reach)", group: 'Holds', world: '16d71:Skyrim.esm', pos: [-174024, 5255, -3322], angleZ: 269 },
  { id: 'falkreath', name: "Falkreath", group: 'Holds', world: '3c:Skyrim.esm', pos: [-30296, -86284, -3107], angleZ: 238 },
  { id: 'whiterun', name: "Whiterun", group: 'Holds', world: '1a26f:Skyrim.esm', pos: [19855, -7423, -3583], angleZ: 0 },
  { id: 'dawnstar', name: "Dawnstar (The Pale)", group: 'Holds', world: '3c:Skyrim.esm', pos: [30808, 106138, -13921], angleZ: 0 },
  { id: 'riften', name: "Riften (The Rift)", group: 'Holds', world: '16bb4:Skyrim.esm', pos: [173415, -96600, 11104], angleZ: 0 },
  { id: 'winterhold', name: "Winterhold", group: 'Holds', world: '3c:Skyrim.esm', pos: [109208, 102864, -9013], angleZ: 350 },
  { id: 'windhelm', name: "Windhelm (Eastmarch)", group: 'Holds', world: '1691d:Skyrim.esm', pos: [135026, 35597, -12469], angleZ: 0 },
  // Outside the walls
  { id: 'solitude-gate', name: "Solitude, east gate", group: 'Outside the walls', world: '3c:Skyrim.esm', pos: [-56887, 105135, -13686], angleZ: 52 },
  { id: 'markarth-stables', name: "Markarth stables", group: 'Outside the walls', world: '3c:Skyrim.esm', pos: [-171097, 6922, -3937], angleZ: 0 },
  { id: 'whiterun-stables', name: "Whiterun stables", group: 'Outside the walls', world: '3c:Skyrim.esm', pos: [18313, -10665, -4590], angleZ: 0 },
  { id: 'riften-stables', name: "Riften stables", group: 'Outside the walls', world: '3c:Skyrim.esm', pos: [173137, -90912, 11102], angleZ: 79 },
  { id: 'windhelm-stables', name: "Windhelm stables", group: 'Outside the walls', world: '3c:Skyrim.esm', pos: [135886, 26101, -12348], angleZ: 0 },
  // Towns
  { id: 'riverwood', name: "Riverwood", group: 'Towns', world: '3c:Skyrim.esm', pos: [21941, -44792, -140], angleZ: 0 },
  { id: 'rorikstead', name: "Rorikstead", group: 'Towns', world: '3c:Skyrim.esm', pos: [-84302, 6952, -4298], angleZ: 320 },
  { id: 'ivarstead', name: "Ivarstead", group: 'Towns', world: '3c:Skyrim.esm', pos: [76375, -64263, 10819], angleZ: 59 },
  { id: 'dragon-bridge', name: "Dragon Bridge", group: 'Towns', world: '3c:Skyrim.esm', pos: [-102148, 82900, -12210], angleZ: 225 },
  { id: 'karthwasten', name: "Karthwasten", group: 'Towns', world: '3c:Skyrim.esm', pos: [-132748, 38549, -6194], angleZ: 254 },
  { id: 'shors-stone', name: "Shor's Stone", group: 'Towns', world: '3c:Skyrim.esm', pos: [162924, -60530, 7162], angleZ: 245 },
  { id: 'kynesgrove', name: "Kynesgrove", group: 'Towns', world: '3c:Skyrim.esm', pos: [134947, 11395, -12200], angleZ: 93 },
  { id: 'helgen', name: "Helgen", group: 'Towns', world: '3c:Skyrim.esm', pos: [18350, -79481, 9204], angleZ: 85 },
  { id: 'old-hroldan', name: "Old Hroldan", group: 'Towns', world: '3c:Skyrim.esm', pos: [-114542, -6796, -5304], angleZ: 85 },
  { id: 'darkwater', name: "Darkwater Crossing", group: 'Towns', world: '3c:Skyrim.esm', pos: [120428, -33954, -9782], angleZ: 26 },
  { id: 'stonehills', name: "Stonehills", group: 'Towns', world: '3c:Skyrim.esm', pos: [-7283, 64267, -11462], angleZ: 95 },
  { id: 'granite-hill', name: "Granite Hill", group: 'Towns', world: '3c:Skyrim.esm', pos: [-54411, -32391, -2686], angleZ: 314 },
  { id: 'oakwood', name: "Oakwood", group: 'Towns', world: '3c:Skyrim.esm', pos: [-15910, -65554, 790], angleZ: 161 },
  { id: 'northkeep', name: "Northkeep", group: 'Towns', world: '3c:Skyrim.esm', pos: [-58535, -75801, 34], angleZ: 331 },
  { id: 'oars-rest', name: "Oar's Rest", group: 'Towns', world: '3c:Skyrim.esm', pos: [-44871, -47144, 719], angleZ: 0 },
  { id: 'markarth-side', name: "Markarth Side", group: 'Towns', world: '3c:Skyrim.esm', pos: [-85904, 57896, -11569], angleZ: 0 },
  // Landmarks
  { id: 'high-hrothgar', name: "High Hrothgar", group: 'Landmarks', world: '3c:Skyrim.esm', pos: [52071, -36267, 22569], angleZ: 176 },
  { id: 'college', name: "College of Winterhold", group: 'Landmarks', world: '3c:Skyrim.esm', pos: [116259, 111530, -7720], angleZ: 45 },
];

// Lands a little above the marker, so nobody ends up under the ground
const TELEPORT_LIFT = 64;

const placeKey = (text) => text.toLowerCase().replace(/[^a-z0-9]/g, '');

// An exact id or name first, then the only place starting with the text
const findPlace = (text) => {
  const key = placeKey(text);
  if (!key) return { place: null, matches: [] };
  const exact = PLACES.find((p) => placeKey(p.id) === key || placeKey(p.name) === key);
  if (exact) return { place: exact, matches: [exact] };
  const matches = PLACES.filter((p) => placeKey(p.id).startsWith(key) || placeKey(p.name).startsWith(key));
  return { place: matches.length === 1 ? matches[0] : null, matches };
};

const teleportTo = (actorId, place) => {
  const [x, y, z] = place.pos;
  mp.set(actorId, 'locationalData', { cellOrWorldDesc: place.world, pos: [x, y, z + TELEPORT_LIFT], rot: [0, 0, place.angleZ] });
};

const listPlaces = (actorId) => {
  system(actorId, 'Places (/tp <place> [ID]):');
  for (const group of [...new Set(PLACES.map((p) => p.group))]) {
    system(actorId, `  ${group}: ${PLACES.filter((p) => p.group === group).map((p) => p.id).join(', ')}`);
  }
};

const teleportCommand = (actorId, argsText) => {
  if (!isAdmin(actorId)) {
    system(actorId, 'Only admins can teleport.');
    return;
  }
  const [placeText, charText] = argsText ? argsText.split(' ') : [];
  if (!placeText || placeText.toLowerCase() === 'list') {
    listPlaces(actorId);
    return;
  }
  const { place, matches } = findPlace(placeText);
  if (!place) {
    system(actorId, matches.length
      ? `"${placeText}" could be ${matches.map((p) => p.id).join(', ')}.`
      : `No place "${placeText}". /tp lists them.`);
    return;
  }
  let targetId = actorId;
  if (charText) {
    const charId = charText.toUpperCase();
    targetId = actorOfCharId(charId);
    if (targetId === null || !isOnline(targetId)) {
      system(actorId, `Nobody online has the ID ${charId}.`);
      return;
    }
  }
  teleportTo(targetId, place);
  console.log(`[tp] ${getName(actorId)} [${getCharId(actorId)}] sent ${getName(targetId)} [${getCharId(targetId)}] to ${place.id}`);
  if (targetId === actorId) {
    system(actorId, `You travel to ${place.name}.`);
  } else {
    system(actorId, `You sent ${label(actorId, targetId)} to ${place.name}.`);
    system(targetId, `An admin moved you to ${place.name}.`);
  }
};

// ---------------------------------------------------------------------------
// Crafting: every recipe visible from the start (until RP professions exist)
// ---------------------------------------------------------------------------
//
// Recipes don't check skill levels; they hide behind perks (Steel Smithing and the
// other material perks, Arcane Blacksmith for enchanted items, the Moon Monk
// robes' own perk). The server doesn't check perks when someone crafts (it
// passes condition functions it doesn't know), so only the crafting menu hides
// them. Perks live in each player's own game and every login starts without
// them, so the server keeps the list on the character (rpPerks, seen only by its
// owner) and the player's game adds any it lacks. Side effect, as in vanilla: the
// material perks also double how far those items can be improved.

const CRAFTING_PERKS = [
  [0x0cb40d, 'Skyrim.esm'], // SteelSmithing
  [0x0cb40f, 'Skyrim.esm'], // ElvenSmithing
  [0x0cb40e, 'Skyrim.esm'], // DwarvenSmithing
  [0x0cb414, 'Skyrim.esm'], // AdvancedArmors
  [0x0cb410, 'Skyrim.esm'], // OrcishSmithing
  [0x0cb411, 'Skyrim.esm'], // GlassSmithing
  [0x0cb412, 'Skyrim.esm'], // EbonySmithing
  [0x0cb413, 'Skyrim.esm'], // DaedricSmithing
  [0x052190, 'Skyrim.esm'], // DragonArmor
  [0x05218e, 'Skyrim.esm'], // ArcaneBlacksmith
  [0x00088c, 'Kad_MoonMonkRobes.esp'], // TeachMoonMonkPerk
];

const PERKS_UPDATE_OWNER = `
  const CHECK_MS = 5000;
  const state = ctx.state;
  const now = Date.now();
  if (now < (state.rpPerksCheckAt || 0)) return;
  state.rpPerksCheckAt = now + CHECK_MS;
  const perks = ctx.value && Array.isArray(ctx.value.perks) ? ctx.value.perks : [];
  const player = ctx.sp.Game.getPlayer();
  if (!player) return;
  for (const [id, file] of perks) {
    const perk = ctx.sp.Perk.from(ctx.sp.Game.getFormFromFile(id, file));
    if (perk && !player.hasPerk(perk)) player.addPerk(perk);
  }
`;

const grantCraftingPerks = (actorId) => mp.set(actorId, 'rpPerks', { perks: CRAFTING_PERKS });

mp.makeProperty('rpPerks', {
  isVisibleByOwner: true,
  isVisibleByNeighbors: false,
  updateOwner: PERKS_UPDATE_OWNER,
  updateNeighbor: '',
});

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
    ensureIdentity(actorId);
    refreshKnown(actorId);
    grantCraftingPerks(actorId);
    if (isAdmin(actorId)) {
      mp.set(actorId, 'consoleCommandsAllowed', true);
      console.log(`[numidium] ${getName(actorId)} (${actorId.toString(16)}) is an admin: dev commands and console on`);
    }
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

// Server-side only: the player's body double, see toggleBodyDouble
mp.makeProperty('rpBodyDouble', {
  isVisibleByOwner: false,
  isVisibleByNeighbors: false,
  updateOwner: '',
  updateNeighbor: '',
});

mp.makeEventSource('_rpBodySave', BODY_EVENT_SOURCE);

mp._rpBodySave = (actorId, data) => {
  // RaceMenu just closed: a name typed there arrives with the appearance, around now
  for (const delay of [500, 3000]) {
    setTimeout(() => {
      try {
        ensureIdentity(actorId);
      } catch (e) {
        console.error('[identity] failed to update identity', e);
      }
    }, delay);
  }
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
    const doubleId = mp.get(actorId, 'rpBodyDouble');
    if (typeof doubleId === 'number') saveBody(doubleId, body);
  } catch (e) {
    console.error('[body] failed to save body', e);
  }
};

hideLeftoverBodyDoubles();

console.log(`[numidium] gamemode loaded: roleplay chat, RaceMenu body sync, `
  + `${factionData ? Object.keys(factionData.factions).length : 'no'} factions`);

