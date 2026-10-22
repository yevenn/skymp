import { Game, Utility, printConsole, createText, setTextSize } from "skyrimPlatform";
import { getScreenResolution } from "../../view/formView";
import { ClientListener, CombinedController, Sp } from "./clientListener";
import { Mod } from "../messages_http/serverManifest";
import { logTrace } from "../../logging";
import { SettingsService } from "./settingsService";

const STATE_KEY = 'loadOrderCheckState';

interface State {
  statusTextId?: number;
};

export class LoadOrderVerificationService extends ClientListener {
  constructor(private sp: Sp, private controller: CombinedController) {
    super();
    this.controller.once("update", () => this.onceUpdate());
  }

  private onceUpdate() {
    // An exception escaping this 'update' handler has frozen the game right
    // after loading in, so never let one out
    try {
      this.verifyLoadOrder();
    } catch (e) {
      printConsole('Load order check crashed:', e);
      this.updateText('LOAD ORDER CHECK FAILED!\nCheck console for details.', [255, 0, 0, 1]);
    }
  }

  private verifyLoadOrder() {
    const settingsService = this.controller.lookupListener(SettingsService);

    this.resetText();
    // The game numbers regular and light plugins separately (light ones share
    // the FE prefix), so each list has to match on its own
    const clientMods = this.getClientMods();
    const clientLightMods = this.getClientLightMods();
    this.printModOrder('Client load order:', clientMods);
    this.printModOrder('Client light plugins:', clientLightMods);
    return settingsService.getServerMods()
      .then((allServerMods) => {
        // The manifest also lists .bsa archives, which aren't part of the load order
        const serverPlugins = allServerMods.filter((mod) => this.isPlugin(mod.filename));
        const serverMods = serverPlugins.filter((mod) => !mod.isLight);
        const serverLightMods = serverPlugins.filter((mod) => mod.isLight);
        this.printModOrder('Server load order:', serverMods);
        this.printModOrder('Server light plugins:', serverLightMods);

        const fail = [
          ...this.compareMods('mod', clientMods, serverMods),
          ...this.compareMods('light plugin', clientLightMods, serverLightMods),
        ];
        if (fail.length !== 0) {
          throw new Error('Load order check failed! ' + fail.join('; '));
        }
      })
      .catch((err) => {
        printConsole(err);
        if (this.sp.settings['skymp5-client']['ignoreLoadOrderMismatch']) {
          this.updateText(
            'LOAD ORDER ERROR!\nHowever, ignoring it because of ignoreLoadOrderMismatch being set.' +
            '\nExpect EVERYTHING BREAK, unless you know what you are doing.\nCheck console for details.' +
            '\nThis message will disappear after 30 seconds.',
            [255, 0, 0, 1], 30,
          );
          return;
        }
        this.updateText(
          'LOAD ORDER ERROR!\nCheck console for details.',
          [255, 0, 0, 1],
        );
      });
  };

  private getState(): State {
    if (typeof this.sp.storage[STATE_KEY] !== 'object') {
      return {};
    }
    return this.sp.storage[STATE_KEY] as State;
  };

  private setState(replacement: State) {
    const oldState = this.sp.storage[STATE_KEY] = this.getState();
    for (const [k, v] of Object.entries(replacement)) {
      (oldState as Record<string, any>)[k] = v;
    }
  };

  private resetText() {
    let { statusTextId } = this.getState();
    if (statusTextId) {
      this.sp.destroyText(statusTextId);
      statusTextId = undefined;
      this.setState({ statusTextId });
    }
  };

  private updateText(text: string, color: [number, number, number, number], clearDelay?: number) {
    const { width, height } = getScreenResolution();
    this.resetText();
    const statusTextId = createText(width / 2, height / 2, text, color);
    setTextSize(statusTextId, 0.5);
    this.setState({ statusTextId });
    if (clearDelay) {
      Utility.wait(clearDelay).then(() => this.resetText());
    }
  }

  private enumerateClientMods(getCount: (() => number), getAt: ((idx: number) => string)) {
    const result = [];
    for (let i = 0; i < getCount(); ++i) {
      const filename = getAt(i);
      const { crc32, size } = this.getFileInfoSafe(filename);
      result.push({ filename, crc32, size });
    }
    return result;
  }

  private getClientMods() {
    return this.enumerateClientMods(Game.getModCount, Game.getModName);
  };

  private getClientLightMods() {
    return this.enumerateClientMods(Game.getLightModCount, Game.getLightModName);
  };

  private isPlugin(filename: string) {
    return /\.(esp|esm|esl)$/i.test(filename);
  }

  // Returns a description of each mismatch, empty if the lists match
  private compareMods(kind: string, clientMods: Mod[], serverMods: Mod[]): string[] {
    const fail = [];
    if (clientMods.length < serverMods.length) {
      fail.push(`missing some server ${kind}s: server has ${serverMods.length}, we have ${clientMods.length}`);
    }
    if (clientMods.length > serverMods.length) {
      this.updateText(
        `LOAD ORDER WARNING: you have more ${kind}s than server!\n(or could not receive server mod list)\nCheck console for details.`,
        [255, 255, 0, 1], 5,
      );
    }
    for (let i = 0; i < Math.min(clientMods.length, serverMods.length); ++i) {
      // Need case-insensitive check for 1.6+
      if (
        clientMods[i].filename.toLowerCase() !== serverMods[i].filename.toLowerCase() ||
        clientMods[i].size !== serverMods[i].size ||
        clientMods[i].crc32 !== serverMods[i].crc32
      ) {
        fail.push(`${kind} #${i} does not match`);
        printConsole(`${i}-th ${kind} (numbered from 0) does not match.`);
        printConsole(`Server has ${JSON.stringify(serverMods[i])}`);
        printConsole(`We have ${JSON.stringify(clientMods[i])}`);
      }
    }
    return fail;
  }

  private printModOrder(header: string, order: Mod[]) {
    printConsole(header);
    for (const [i, mod] of Object.entries(order)) {
      printConsole(`#${i} ${JSON.stringify(mod)}`);
    }
  };

  // SkyrimPlatform may throw plain strings rather than Error objects, so any
  // failure means "unknown" here instead of escaping
  private getFileInfoSafe(filename: string) {
    try {
      return this.sp.getFileInfo(filename);
    } catch (e) {
      logTrace(this, `Failed to get file info for`, filename, '-', e);
      return { crc32: 0, size: 0 };
    }
  }
}
