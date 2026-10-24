import * as crypto from "crypto";
import * as fs from "fs";

// Numidium's own sign-in, instead of SkyMP's master server: the launcher signs
// the player in with Discord (OAuth2, scope "identify") and sends the code it
// gets back here; the server trades it for the player's Discord id with the
// Discord application's secret, and returns a session. The game sends that
// session when it connects, and Login turns it into the player's profile.
//
// server-settings.json:
//   "offlineMode": false,
//   "numidiumAuth": {
//     "discordClientId": "...",       // public, also in manifest.json for the launcher
//     "discordClientSecret": "...",   // never commit it
//     "redirectPort": 53134,          // the launcher listens on http://127.0.0.1:<port>/callback
//     "sessionDays": 30,
//     "admins": ["<discord id>"],     // dev commands and the console
//     "profiles": { "<discord id>": 1 } // existing characters to keep
//   }

export interface NumidiumAuthSettings {
  discordClientId: string;
  discordClientSecret: string;
  redirectPort?: number;
  sessionDays?: number;
  admins?: string[];
  profiles?: Record<string, number>;
}

interface Account {
  profileId: number;
  username: string;
  createdAt: string;
}

interface Session {
  discordId: string;
  expiresAt: number;
}

interface Store {
  nextProfileId: number;
  accounts: Record<string, Account>;
  // Keyed by the session's SHA-256, so the file never holds a usable session
  sessions: Record<string, Session>;
}

export interface SignedIn {
  session: string;
  discordId: string;
  username: string;
  profileId: number;
  expiresAt: number;
}

// Profile ids below this are dev profiles from offline mode
const kFirstProfileId = 100;
const kDefaultRedirectPort = 53134;

export const parseNumidiumAuth = (raw: unknown): NumidiumAuthSettings | null => {
  if (!raw || typeof raw !== "object") return null;
  const s = raw as NumidiumAuthSettings;
  if (typeof s.discordClientId !== "string" || !s.discordClientId) {
    console.error("numidiumAuth.discordClientId is missing, Discord sign-in is off");
    return null;
  }
  if (typeof s.discordClientSecret !== "string" || !s.discordClientSecret) {
    console.error("numidiumAuth.discordClientSecret is missing, Discord sign-in is off");
    return null;
  }
  return s;
};

export const redirectUriFor = (settings: NumidiumAuthSettings) =>
  `http://127.0.0.1:${settings.redirectPort || kDefaultRedirectPort}/callback`;

const hashSession = (session: string) => crypto.createHash("sha256").update(session).digest("hex");

export class NumidiumAccounts {
  constructor(
    private settings: NumidiumAuthSettings,
    private storePath = "numidium-accounts.json",
    private fetchFn: typeof fetch = global.fetch,
  ) {
    this.store = this.load();
  }

  get publicInfo() {
    return { type: "discord", discordClientId: this.settings.discordClientId, redirectUri: redirectUriFor(this.settings) };
  }

  isAdmin(discordId: string | undefined | null): boolean {
    return !!discordId && (this.settings.admins || []).includes(discordId);
  }

  // Trades the launcher's OAuth2 code for the Discord user, and signs them in
  async signIn(code: string, redirectUri: string): Promise<SignedIn> {
    if (redirectUri !== redirectUriFor(this.settings)) {
      throw new AuthError(400, `redirectUri must be ${redirectUriFor(this.settings)}`);
    }
    const tokenResponse = await this.fetchFn("https://discord.com/api/oauth2/token", {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        client_id: this.settings.discordClientId,
        client_secret: this.settings.discordClientSecret,
        grant_type: "authorization_code",
        code,
        redirect_uri: redirectUri,
      }).toString(),
    });
    if (!tokenResponse.ok) {
      throw new AuthError(401, `Discord refused the sign-in code (${tokenResponse.status})`);
    }
    const token = await tokenResponse.json() as { access_token?: string };
    if (!token.access_token) throw new AuthError(502, "Discord sent no access token");

    const userResponse = await this.fetchFn("https://discord.com/api/users/@me", {
      headers: { Authorization: `Bearer ${token.access_token}` },
    });
    if (!userResponse.ok) throw new AuthError(502, `Discord didn't say who signed in (${userResponse.status})`);
    const user = await userResponse.json() as { id?: string; username?: string; global_name?: string | null };
    if (!user.id) throw new AuthError(502, "Discord sent no user id");

    const account = this.accountFor(user.id, user.global_name || user.username || user.id);
    const session = crypto.randomBytes(32).toString("hex");
    const expiresAt = Date.now() + (this.settings.sessionDays || 30) * 24 * 3600 * 1000;
    this.dropExpired();
    this.store.sessions[hashSession(session)] = { discordId: user.id, expiresAt };
    this.save();
    console.log(`[auth] ${account.username} (Discord ${user.id}) signed in as profile ${account.profileId}`);
    return { session, discordId: user.id, username: account.username, profileId: account.profileId, expiresAt };
  }

  // The signed-in player a session belongs to, or null when it's unknown or expired
  resolve(session: unknown): { profileId: number; discordId: string; username: string; expiresAt: number } | null {
    if (typeof session !== "string" || !session) return null;
    const found = this.store.sessions[hashSession(session)];
    if (!found || found.expiresAt < Date.now()) return null;
    const account = this.store.accounts[found.discordId];
    if (!account) return null;
    return { profileId: account.profileId, discordId: found.discordId, username: account.username, expiresAt: found.expiresAt };
  }

  signOut(session: unknown): void {
    if (typeof session !== "string") return;
    if (delete this.store.sessions[hashSession(session)]) this.save();
  }

  private accountFor(discordId: string, username: string): Account {
    let account = this.store.accounts[discordId];
    if (!account) {
      const assigned = this.settings.profiles?.[discordId];
      account = {
        profileId: typeof assigned === "number" ? assigned : this.store.nextProfileId++,
        username,
        createdAt: new Date().toISOString(),
      };
      this.store.accounts[discordId] = account;
    }
    account.username = username;
    return account;
  }

  private dropExpired() {
    const now = Date.now();
    for (const [key, session] of Object.entries(this.store.sessions)) {
      if (session.expiresAt < now) delete this.store.sessions[key];
    }
  }

  private load(): Store {
    if (fs.existsSync(this.storePath)) {
      const store = JSON.parse(fs.readFileSync(this.storePath, "utf8")) as Store;
      store.accounts = store.accounts || {};
      store.sessions = store.sessions || {};
      store.nextProfileId = Math.max(store.nextProfileId || 0, kFirstProfileId);
      return store;
    }
    return { nextProfileId: kFirstProfileId, accounts: {}, sessions: {} };
  }

  private save() {
    const temp = this.storePath + ".tmp";
    fs.writeFileSync(temp, JSON.stringify(this.store, null, 2));
    fs.renameSync(temp, this.storePath);
  }

  private store: Store;
}

export class AuthError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

let gAccounts: NumidiumAccounts | null = null;

// The server's one account store, when numidiumAuth is set up
export const getNumidiumAccounts = (rawSettings: unknown): NumidiumAccounts | null => {
  if (!gAccounts) {
    const settings = parseNumidiumAuth(rawSettings);
    if (settings) gAccounts = new NumidiumAccounts(settings);
  }
  return gAccounts;
};
