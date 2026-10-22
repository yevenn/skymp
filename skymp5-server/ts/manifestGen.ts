import { Settings } from "./settings";
import * as crc32 from "crc-32";
import * as path from "path";
import * as fs from "fs";

interface ManifestModEntry {
  filename: string;
  crc32: number;
  size: number;
  // Plugins only: loaded as a light plugin (ESL flag or .esl extension)
  isLight?: boolean;
}

// TES4 record header: type (4 bytes), data size (4), flags (4)
const kTes4FlagsOffset = 8;
const kLightPluginFlag = 0x200;

const isLightPlugin = (espmName: string, buf: Uint8Array) => {
  if (espmName.toLowerCase().endsWith(".esl")) {
    return true;
  }
  if (buf.length < kTes4FlagsOffset + 4) {
    return false;
  }
  const flags = Buffer.from(buf.buffer, buf.byteOffset, buf.length).readUInt32LE(kTes4FlagsOffset);
  return (flags & kLightPluginFlag) !== 0;
};

interface Manifest {
  versionMajor: number;
  mods: Array<ManifestModEntry>;
  loadOrder: Array<string>;
}

const getBsaNameByEspmName = (espmName: string) => {
  if (espmName.endsWith(".esp") || espmName.endsWith(".esm") || espmName.endsWith(".esl")) {
    const nameNoExt = espmName.split(".").slice(0, -1).join(".");
    return nameNoExt + ".bsa";
  }
  throw new Error(`'${espmName}' is not a valid esp or esm name`);
};

export const generateManifest = (settings: Settings): void => {
  const manifest: Manifest = {
    mods: [],
    versionMajor: 1,
    loadOrder: settings.loadOrder.map(x => path.basename(x)),
  };

  settings.loadOrder.forEach((loadOrderElement) => {
    const espmName = path.isAbsolute(loadOrderElement)
      ? path.basename(loadOrderElement)
      : loadOrderElement;

    const espmPath = path.isAbsolute(loadOrderElement)
      ? loadOrderElement
      : path.join(settings.dataDir, espmName);

    const buf: Uint8Array = fs.readFileSync(espmPath);
    manifest.mods.push({
      crc32: crc32.buf(buf),
      filename: espmName,
      size: buf.length,
      isLight: isLightPlugin(espmName, buf),
    });

    const bsaName = getBsaNameByEspmName(espmName);
    const bsaPath = path.join(settings.dataDir, bsaName);
    if (fs.existsSync(bsaPath)) {
      const buf: Uint8Array = fs.readFileSync(bsaPath);
      manifest.mods.push({
        crc32: crc32.buf(buf),
        filename: bsaName,
        size: buf.length,
      });
    }
  });

  const manifestPath = path.join(settings.dataDir, "manifest.json");
  fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 4));
};
