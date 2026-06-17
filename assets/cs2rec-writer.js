(function () {
  const REC_MAGIC = new Uint8Array([67, 83, 50, 66, 77, 82, 69, 67]);
  const MOVETYPE_LADDER = 9;

  class ByteWriter {
    constructor() {
      this.parts = [];
      this.length = 0;
    }

    pushBytes(bytes) {
      const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
      this.parts.push(view);
      this.length += view.byteLength;
    }

    pushU8(value) {
      this.pushBytes(Uint8Array.of(Number(value) & 0xff));
    }

    pushU16(value) {
      const bytes = new Uint8Array(2);
      new DataView(bytes.buffer).setUint16(0, Number(value) & 0xffff, true);
      this.pushBytes(bytes);
    }

    pushU32(value) {
      const bytes = new Uint8Array(4);
      new DataView(bytes.buffer).setUint32(0, Number(value) >>> 0, true);
      this.pushBytes(bytes);
    }

    pushU64(value) {
      const bytes = new Uint8Array(8);
      new DataView(bytes.buffer).setBigUint64(0, toU64(value), true);
      this.pushBytes(bytes);
    }

    pushF32(value) {
      const bytes = new Uint8Array(4);
      new DataView(bytes.buffer).setFloat32(0, finiteNumber(value, 0), true);
      this.pushBytes(bytes);
    }

    pushString(value) {
      const encoded = new TextEncoder().encode(value === undefined || value === null ? "" : String(value));
      const length = Math.min(encoded.length, 0xffff);
      this.pushU16(length);
      this.pushBytes(encoded.subarray(0, length));
    }

    concat() {
      const out = new Uint8Array(this.length);
      let offset = 0;
      for (const part of this.parts) {
        out.set(part, offset);
        offset += part.byteLength;
      }
      return out;
    }
  }

  function buildBundle(entries, brotli) {
    const bundle = new ByteWriter();
    writeVarUint(bundle, entries.length);
    for (const entry of entries) {
      bundle.pushString(entry.key);
      writeVarUint(bundle, entry.payload.byteLength);
      bundle.pushBytes(entry.payload);
    }

    const compressed = brotli.compress(bundle.concat(), { quality: 6 });
    const output = new ByteWriter();
    output.pushBytes(REC_MAGIC);
    output.pushU32(4);
    output.pushU8(2);
    output.pushBytes(compressed);
    return output.concat();
  }

  function buildRouteEntry(route, options) {
    const frames = route.frames;
    if (!frames || frames.length < 2) {
      return null;
    }
    inferVelocities(frames, route.tickrate);

    const transformDownsample = Math.max(1, Number(options.downsample) || 1);
    const snapshots = frames.map(snapshotBytes);
    const weaponDefs = [];
    const subtickCounts = [];
    const subtickRecords = [];
    for (let i = 0; i < frames.length - 1; i++) {
      const tick = intValue(frames[i].tick, 0);
      const subticks = route.subticksByTick ? (route.subticksByTick.get(tick) || []) : [];
      let subtickCount = 0;
      for (const subtick of subticks) {
        const compact = compactSubtick(subtick);
        if (compact) {
          subtickRecords.push(compact);
          subtickCount++;
        }
      }
      subtickCounts.push(subtickCount);
      let weaponDef = weaponDefFromFrame(frames[i]);
      if (weaponDef < 0) {
        weaponDef = weaponDefFromFrame(frames[i + 1]);
      }
      weaponDefs.push(weaponDef);
    }

    const payload = new ByteWriter();
    payload.pushF32(route.tickrate);
    payload.pushU32(Math.max(0, intValue(route.roundNumber, 0)));
    payload.pushU8(Math.max(0, Math.min(255, intValue(route.teamNum, 0))));
    payload.pushU32(0);
    payload.pushU64(route.steamId || 0);
    writeVarUint(payload, weaponDefs.length);
    writeVarUint(payload, subtickRecords.length);
    writeVarUint(payload, snapshots.length);
    writeVarUint(payload, transformDownsample);

    const sampleIndexes = sampledTransformIndexes(snapshots.length, transformDownsample);
    writeVarUint(payload, sampleIndexes.length);
    payload.pushString(route.mapName || "");
    payload.pushString(route.playerName || "");

    for (const index of sampleIndexes) {
      writeVarUint(payload, index);
      payload.pushBytes(snapshots[index].subarray(0, 12));
    }

    for (const snapshot of snapshots) {
      payload.pushBytes(snapshot.subarray(24, 36));
    }

    const moveTypes = snapshots.map(snapshot => snapshot[40]);
    for (const snapshot of snapshots) {
      payload.pushBytes(snapshot.subarray(12, 24));
    }

    writeRleVarInts(payload, snapshots.map(snapshot => readU32(snapshot, 36)), false);
    writeRleVarInts(payload, moveTypes, false);
    writeU64Rle(payload, snapshots.map(snapshot => readU64(snapshot, 44)));
    writeSparseU64Rle(payload, snapshots.map(snapshot => readU64(snapshot, 52)));
    writeSparseU64Rle(payload, snapshots.map(snapshot => readU64(snapshot, 60)));
    writeFloatRle(payload, snapshots.map(snapshot => snapshot.subarray(68, 72)));
    writeFloatRle(payload, snapshots.map(snapshot => snapshot.subarray(72, 76)));
    writeRleVarInts(payload, snapshots.map(snapshot => snapshot[88]), false);
    writeRleVarInts(payload, snapshots.map(snapshot => snapshot[89]), false);
    writeRleVarInts(payload, snapshots.map(snapshot => snapshot[90]), false);
    writeSparseVarUintOverrideRle(payload, snapshots.map(snapshot => snapshot[91]), moveTypes);
    writeSparseVec3Rle(payload, snapshots.map((snapshot, index) =>
      moveTypes[index] === MOVETYPE_LADDER ? snapshot.subarray(76, 88) : new Uint8Array(12)));
    writeRleVarInts(payload, weaponDefs, true);
    writeRleVarInts(payload, subtickCounts, false);
    for (const subtick of subtickRecords) {
      payload.pushBytes(subtick);
    }

    return {
      key: route.key,
      payload: payload.concat(),
      weaponDefs
    };
  }

  function routeSegmentInfo(route, weaponDefs) {
    const frames = route.frames;
    const duration = frames.length > 1
      ? Math.max(0, finiteNumber(frames[frames.length - 1].timeSeconds, 0) - finiteNumber(frames[0].timeSeconds, 0))
      : 0;
    const normalized = weaponDefs.map(normalizeWeaponDefIndex).filter(value => value >= 0);
    return {
      duration,
      firstWeaponDefIndex: normalized.length ? normalized[0] : -1,
      preloadWeaponDefIndexes: [...new Set(normalized.filter(isPreloadWeaponDef))].sort((a, b) => a - b),
      startFrame: manifestFrame(frames[0], 0),
      endFrame: manifestFrame(frames[frames.length - 1], duration)
    };
  }

  function snapshotBytes(frame) {
    const bytes = new Uint8Array(92);
    const view = new DataView(bytes.buffer);
    const floats = [
      frame.x, frame.y, frame.z,
      frame.velocityX, frame.velocityY, frame.velocityZ,
      frame.pitch, frame.yaw, frame.roll
    ];
    for (let i = 0; i < floats.length; i++) {
      view.setFloat32(i * 4, finiteNumber(floats[i], 0), true);
    }
    view.setUint32(36, intValue(frame.entityFlags, 1) >>> 0, true);
    bytes[40] = intValue(frame.moveType, 2) & 0xff;
    view.setBigUint64(44, toU64(frame.buttons), true);
    view.setBigUint64(52, toU64(frame.buttons1), true);
    view.setBigUint64(60, toU64(frame.buttons2), true);
    view.setFloat32(68, finiteNumber(frame.duckAmount, 0), true);
    view.setFloat32(72, finiteNumber(frame.duckSpeed, 0), true);
    view.setFloat32(76, finiteNumber(frame.ladderNormalX, 0), true);
    view.setFloat32(80, finiteNumber(frame.ladderNormalY, 0), true);
    view.setFloat32(84, finiteNumber(frame.ladderNormalZ, 0), true);
    bytes[88] = intValue(frame.ducked, 0) & 0xff;
    bytes[89] = intValue(frame.ducking, 0) & 0xff;
    bytes[90] = intValue(frame.desiresDuck, 0) & 0xff;
    bytes[91] = intValue(frame.actualMoveType, frame.moveType === undefined || frame.moveType === null ? 2 : frame.moveType) & 0xff;
    return bytes;
  }

  function manifestFrame(frame, timeSeconds) {
    const payload = {
      relativeTick: intValue(frame.relativeTick, 0),
      time: roundFloat(timeSeconds),
      x: roundFloat(frame.x),
      y: roundFloat(frame.y),
      z: roundFloat(frame.z),
      pitch: roundFloat(frame.pitch),
      yaw: roundFloat(frame.yaw),
      buttons: Number(toU64(frame.buttons) & BigInt(0xffffffff)),
      activeWeapon: frame.activeWeapon || ""
    };
    const def = weaponDefFromFrame(frame);
    if (def >= 0) {
      payload.activeWeaponDefIndex = def;
    }
    return payload;
  }

  function inferVelocities(frames, tickrate) {
    for (let i = 0; i < frames.length; i++) {
      const frame = frames[i];
      if (hasReasonableVelocity(frame)) {
        continue;
      }
      const prev = frames[Math.max(0, i - 1)];
      const next = frames[Math.min(frames.length - 1, i + 1)];
      const denom = Math.max(1, intValue(next.tick, i) - intValue(prev.tick, i)) / Math.max(1, tickrate);
      frame.velocityX = (finiteNumber(next.x, frame.x) - finiteNumber(prev.x, frame.x)) / denom;
      frame.velocityY = (finiteNumber(next.y, frame.y) - finiteNumber(prev.y, frame.y)) / denom;
      frame.velocityZ = (finiteNumber(next.z, frame.z) - finiteNumber(prev.z, frame.z)) / denom;
    }
  }

  function hasReasonableVelocity(frame) {
    const max = 16384;
    return [frame.velocityX, frame.velocityY, frame.velocityZ].every(value =>
      Number.isFinite(Number(value)) && Math.abs(Number(value)) <= max);
  }

  function compactSubtick(subtick) {
    const optional = [];
    const pressed = finiteNumber(subtick.pressed, 0);
    const analogForward = finiteNumber(subtick.analogForward, 0);
    const analogLeft = finiteNumber(subtick.analogLeft, 0);
    const pitchDelta = finiteNumber(subtick.pitchDelta, 0);
    const yawDelta = finiteNumber(subtick.yawDelta, 0);
    let flags = 0;
    if (pressed !== 0) flags |= 1 << 0;
    if (analogForward !== 0) flags |= 1 << 1;
    if (analogLeft !== 0) flags |= 1 << 2;
    if (pitchDelta !== 0) flags |= 1 << 3;
    if (yawDelta !== 0) flags |= 1 << 4;
    if (!flags && !intValue(subtick.button, 0)) {
      return null;
    }
    const writer = new ByteWriter();
    writer.pushU8(flags);
    writer.pushF32(Math.max(0, Math.min(0.999999, finiteNumber(subtick.when, 0))));
    writer.pushU32(intValue(subtick.button, 0));
    if (flags & (1 << 0)) writer.pushF32(pressed);
    if (flags & (1 << 1)) writer.pushF32(analogForward);
    if (flags & (1 << 2)) writer.pushF32(analogLeft);
    if (flags & (1 << 3)) writer.pushF32(pitchDelta);
    if (flags & (1 << 4)) writer.pushF32(yawDelta);
    return writer.concat();
  }

  function sampledTransformIndexes(count, stride) {
    if (count <= 0) return [];
    const indexes = [];
    const safeStride = Math.max(1, intValue(stride, 1));
    for (let i = 0; i < count; i += safeStride) {
      indexes.push(i);
    }
    if (indexes[indexes.length - 1] !== count - 1) {
      indexes.push(count - 1);
    }
    return indexes;
  }

  function writeVarUint(writer, value) {
    let current = Number(value);
    if (!Number.isFinite(current) || current < 0) {
      throw new Error(`invalid varuint ${value}`);
    }
    current = Math.floor(current);
    while (current >= 0x80) {
      writer.pushU8((current & 0x7f) | 0x80);
      current = Math.floor(current / 128);
    }
    writer.pushU8(current);
  }

  function writeVarInt(writer, value) {
    const current = intValue(value, 0);
    writeVarUint(writer, (current << 1) ^ (current >> 31));
  }

  function writeRleVarInts(writer, values, signed) {
    for (let i = 0; i < values.length;) {
      const value = intValue(values[i], 0);
      let run = 1;
      while (i + run < values.length && intValue(values[i + run], 0) === value) {
        run++;
      }
      signed ? writeVarInt(writer, value) : writeVarUint(writer, value);
      writeVarUint(writer, run);
      i += run;
    }
  }

  function writeU64Rle(writer, values) {
    for (let i = 0; i < values.length;) {
      const value = toU64(values[i]);
      let run = 1;
      while (i + run < values.length && toU64(values[i + run]) === value) {
        run++;
      }
      writer.pushU64(value);
      writeVarUint(writer, run);
      i += run;
    }
  }

  function writeSparseU64Rle(writer, values) {
    const runs = [];
    for (let i = 0; i < values.length;) {
      const value = toU64(values[i]);
      if (value === BigInt(0)) {
        i++;
        continue;
      }
      let run = 1;
      while (i + run < values.length && toU64(values[i + run]) === value) {
        run++;
      }
      runs.push([i, run, value]);
      i += run;
    }
    writeVarUint(writer, runs.length);
    for (const [start, run, value] of runs) {
      writeVarUint(writer, start);
      writeVarUint(writer, run);
      writer.pushU64(value);
    }
  }

  function writeFloatRle(writer, values) {
    const runs = [];
    for (let i = 0; i < values.length;) {
      const value = values[i];
      let run = 1;
      while (i + run < values.length && bytesEqual(values[i + run], value)) {
        run++;
      }
      runs.push([value, run]);
      i += run;
    }
    writeVarUint(writer, runs.length);
    for (const [value, run] of runs) {
      writer.pushBytes(value);
      writeVarUint(writer, run);
    }
  }

  function writeSparseVarUintOverrideRle(writer, values, defaults) {
    const runs = [];
    for (let i = 0; i < values.length;) {
      const value = intValue(values[i], 0);
      if (value === intValue(defaults[i], 0)) {
        i++;
        continue;
      }
      let run = 1;
      while (
        i + run < values.length &&
        intValue(values[i + run], 0) === value &&
        value !== intValue(defaults[i + run], 0)
      ) {
        run++;
      }
      runs.push([i, run, value]);
      i += run;
    }
    writeVarUint(writer, runs.length);
    for (const [start, run, value] of runs) {
      writeVarUint(writer, start);
      writeVarUint(writer, run);
      writeVarUint(writer, value);
    }
  }

  function writeSparseVec3Rle(writer, values) {
    const zero = new Uint8Array(12);
    const runs = [];
    for (let i = 0; i < values.length;) {
      const value = values[i];
      if (bytesEqual(value, zero)) {
        i++;
        continue;
      }
      let run = 1;
      while (i + run < values.length && bytesEqual(values[i + run], value)) {
        run++;
      }
      runs.push([i, run, value]);
      i += run;
    }
    writeVarUint(writer, runs.length);
    for (const [start, run, value] of runs) {
      writeVarUint(writer, start);
      writeVarUint(writer, run);
      writer.pushBytes(value);
    }
  }

  function readU32(bytes, offset) {
    return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(offset, true);
  }

  function readU64(bytes, offset) {
    return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getBigUint64(offset, true);
  }

  function bytesEqual(a, b) {
    if (a.byteLength !== b.byteLength) return false;
    for (let i = 0; i < a.byteLength; i++) {
      if (a[i] !== b[i]) return false;
    }
    return true;
  }

  function weaponDefFromFrame(frame) {
    const explicit = intOrNull(frame.activeWeaponDefIndex);
    if (explicit !== null) {
      return normalizeWeaponDefIndex(explicit);
    }
    return weaponDefIndex(frame.activeWeapon);
  }

  const WEAPON_DEF_INDEXES = new Map([
    ["weapon_deagle", 1], ["weapon_elite", 2], ["weapon_fiveseven", 3], ["weapon_glock", 4],
    ["weapon_ak47", 7], ["weapon_aug", 8], ["weapon_awp", 9], ["weapon_famas", 10],
    ["weapon_g3sg1", 11], ["weapon_galilar", 13], ["weapon_m249", 14], ["weapon_m4a1", 16],
    ["weapon_mac10", 17], ["weapon_p90", 19], ["weapon_mp5sd", 23], ["weapon_ump45", 24],
    ["weapon_xm1014", 25], ["weapon_bizon", 26], ["weapon_mag7", 27], ["weapon_negev", 28],
    ["weapon_sawedoff", 29], ["weapon_tec9", 30], ["weapon_taser", 31], ["weapon_hkp2000", 32],
    ["weapon_mp7", 33], ["weapon_mp9", 34], ["weapon_nova", 35], ["weapon_p250", 36],
    ["weapon_shield", 37], ["weapon_scar20", 38], ["weapon_sg556", 39], ["weapon_ssg08", 40],
    ["weapon_knife", 42], ["weapon_flashbang", 43], ["weapon_hegrenade", 44],
    ["weapon_smokegrenade", 45], ["weapon_molotov", 46], ["weapon_decoy", 47],
    ["weapon_incgrenade", 48], ["weapon_c4", 49], ["weapon_m4a1_silencer", 60],
    ["weapon_usp_silencer", 61], ["weapon_cz75a", 63], ["weapon_revolver", 64]
  ]);

  const PRELOAD_WEAPON_DEFS = new Set([...WEAPON_DEF_INDEXES.entries()]
    .filter(([name, def]) => def >= 0 && !["weapon_knife", "weapon_c4", "weapon_taser"].includes(name))
    .map(([, def]) => def));

  function weaponDefIndex(itemName) {
    const raw = String(itemName || "").toLowerCase();
    if (raw.includes("knife") || raw.includes("bayonet")) {
      return 42;
    }
    const normalized = normalizeItem(raw);
    if (!normalized) return -1;
    if (normalized.includes("knife") || normalized.includes("bayonet")) {
      return 42;
    }
    return WEAPON_DEF_INDEXES.has(normalized) ? WEAPON_DEF_INDEXES.get(normalized) : -1;
  }

  function normalizeWeaponDefIndex(value) {
    const parsed = intValue(value, -1);
    return parsed === 42 || parsed === 59 || parsed === 9001 || (parsed >= 500 && parsed < 600)
      ? 42
      : parsed;
  }

  function isPreloadWeaponDef(value) {
    return PRELOAD_WEAPON_DEFS.has(normalizeWeaponDefIndex(value));
  }

  function normalizeItem(value) {
    const raw = String(value || "").trim().toLowerCase();
    if (!raw) return "";
    if (raw.startsWith("weapon_") || raw.startsWith("item_")) return raw;
    return `weapon_${raw.replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "")}`;
  }

  function roundFloat(value) {
    return Math.round(finiteNumber(value, 0) * 1000) / 1000;
  }

  function finiteNumber(value, fallback) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
  }

  function intValue(value, fallback) {
    if (typeof value === "bigint") return Number(value);
    const parsed = Number(value);
    return Number.isFinite(parsed) ? Math.trunc(parsed) : fallback;
  }

  function intOrNull(value) {
    if (value === undefined || value === null || value === "") return null;
    const parsed = intValue(value, NaN);
    return Number.isFinite(parsed) ? parsed : null;
  }

  function toU64(value) {
    if (typeof value === "bigint") return BigInt.asUintN(64, value);
    if (typeof value === "string" && value.trim()) {
      try {
        return BigInt.asUintN(64, BigInt(value));
      } catch (error) {
        return BigInt(0);
      }
    }
    const parsed = Number(value);
    return Number.isFinite(parsed) && parsed >= 0 ? BigInt.asUintN(64, BigInt(Math.trunc(parsed))) : BigInt(0);
  }

  self.CS2RecWriter = {
    buildBundle,
    buildRouteEntry,
    routeSegmentInfo,
    weaponDefIndex,
    normalizeWeaponDefIndex,
    isPreloadWeaponDef
  };
})();
