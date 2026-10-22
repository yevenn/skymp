export interface Mod {
    filename: string;
    size: number;
    crc32: number;
    // Plugins only: loaded as a light plugin (FE xxx form ids)
    isLight?: boolean;
};

export interface ServerManifest {
    versionMajor: number;
    mods: Mod[];
    loadOrder: string[];
};
