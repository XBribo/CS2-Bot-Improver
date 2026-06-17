/* global wasm_bindgen, CS2RecWriter */

importScripts("../vendor/demoparser/demoparser2.js", "./cs2rec-writer.js");

const BROTLI_MODULE = "https://cdn.jsdelivr.net/npm/brotli-wasm@3.0.1/index.web.js";
const FFLATE_MODULE = "https://cdn.jsdelivr.net/npm/fflate@0.8.2/esm/browser.js";
const WASM_PATH = new URL("../vendor/demoparser/demoparser2_bg.wasm", self.location.href).href;
const DEFAULT_ECONOMY_SAMPLE_SECONDS = 2;

const WANTED_PROPS = unique([
  "X",
  "Y",
  "Z",
  "pitch",
  "yaw",
  "buttons",
  "team_num",
  "player_name",
  "player_steamid",
  "balance",
  "inventory",
  "active_weapon_name",
  "total_rounds_played",
  "is_alive",
  "current_equip_value",
  "round_start_equip_value",
  "armor_value",
  "has_helmet",
  "has_defuser",
  "team_clan_name",
  "round_in_progress",
  "is_freeze_period",
  "velocity_X",
  "velocity_Y",
  "velocity_Z",
  "item_def_idx",
  "inventory_as_ids",
  "move_type",
  "CCSPlayerPawn.m_fFlags",
  "duck_amount",
  "duck_speed",
  "ducked",
  "ducking",
  "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDesiresDuck",
  "usercmd_viewangle_x",
  "usercmd_viewangle_y",
  "usercmd_buttonstate_1",
  "usercmd_buttonstate_2",
  "usercmd_buttonstate_3",
  "usercmd_forward_move",
  "usercmd_left_move",
  "usercmd_weapon_select",
  "usercmd_left_hand_desired",
  "usercmd_attack1_start_history_index",
  "usercmd_attack2_start_history_index",
  "usercmd_input_history",
  "usercmd_subtick_moves",
  "CCSPlayerPawn.m_angEyeAngles",
  "CCSPlayerPawn.m_MoveType",
  "CCSPlayerPawn.m_nActualMoveType",
  "CCSPlayerPawn.CCSPlayer_MovementServices.m_vecLadderNormal"
]);

let runtimePromise;

self.onmessage = event => {
  if (!event.data || event.data.type !== "convert") {
    return;
  }

  convert(event.data.file, event.data.options || {}).catch(error => {
    postMessage({
      type: "error",
      message: (error && (error.stack || error.message)) || String(error)
    });
  });
};

ready().catch(error => {
  postMessage({
    type: "error",
    message: (error && (error.stack || error.message)) || String(error)
  });
});

async function ready() {
  await runtime();
  postMessage({ type: "ready" });
}

async function runtime() {
  if (!runtimePromise) {
    runtimePromise = (async () => {
      await wasm_bindgen(WASM_PATH);
      const brotliModule = await import(BROTLI_MODULE);
      const brotli = await brotliModule.default;
      const fflate = await import(FFLATE_MODULE);
      return { brotli, fflate };
    })();
  }
  return runtimePromise;
}

async function convert(file, rawOptions) {
  const { brotli, fflate } = await runtime();
  const options = normalizeOptions(rawOptions);
  log(`Reading ${file.name} (${formatBytes(file.size)})`);
  const bytes = new Uint8Array(await file.arrayBuffer());

  progress("Reading demo header", 0, 1);
  const header = plain(wasm_bindgen.parseHeader(bytes));
  const mapName = normalizeMapName(
    firstDefined(header && header.map_name, header && header.mapName, header && header.map, header && header.network_protocol)
  );
  if (!mapName) {
    throw new Error("Could not determine map name from demo header.");
  }
  progress("Reading demo events", 0, 1);
  const freezeEvents = parseEventSafe(bytes, "round_freeze_end");
  const roundEndEvents = parseEventSafe(bytes, "round_end");
  const plantEvents = parseEventSafe(bytes, "bomb_planted", ["player_steamid", "X", "Y", "Z"]);
  const freezeTicks = sortedUniqueTicks(freezeEvents);
  if (!freezeTicks.length) {
    throw new Error("No round_freeze_end events found. This demo cannot be converted into opening routes.");
  }

  const segments = buildSegments(freezeTicks, roundEndEvents, plantEvents, options);
  const totalWantedTicks = segments.reduce((sum, segment) => sum + segment.ticks.length, 0);
  log(`Found ${segments.length} rounds on ${mapName}; parsing ${totalWantedTicks.toLocaleString()} demo ticks.`);

  const allRows = [];
  let parsedTicks = 0;
  for (const segment of segments) {
    progress(`Parsing ticks for round ${segment.roundNumber}`, parsedTicks, totalWantedTicks);
    const rows = parseTicksForSegment(bytes, segment.ticks);
    parsedTicks += segment.ticks.length;
    allRows.push(...rows);
    progress(`Parsing ticks for round ${segment.roundNumber}`, parsedTicks, totalWantedTicks);
  }

  if (!allRows.length) {
    throw new Error("Parser returned no player-tick rows for the selected round windows.");
  }

  progress("Building player routes", 0, allRows.length);
  const buildResult = buildDataset(file.name, mapName, segments, allRows, options);
  if (!buildResult.entries.length) {
    throw new Error("No replay routes were produced. The demo may not contain alive players on T/CT after freeze end.");
  }

  progress("Compressing cs2rec bundle", 0, buildResult.entries.length);
  const recBytes = CS2RecWriter.buildBundle(buildResult.entries, brotli);
  const base = sanitizeFileBase(`${mapName}_${stripExtension(file.name)}`);
  const manifestBytes = fflate.strToU8(JSON.stringify(buildResult.manifest, null, 2));
  const readmeBytes = fflate.strToU8([
    "CS2Rec Converter output",
    "",
    `Map: ${mapName}`,
    `Source demo: ${file.name}`,
    `Rounds: ${buildResult.manifest.rounds.length}`,
    `Routes: ${buildResult.entries.length}`,
    "",
    "Place the manifest JSON and .cs2rec bundle under ProOpeningReplay/data.",
    ""
  ].join("\n"));
  const zip = fflate.zipSync({
    [`${base}.cs2rec`]: recBytes,
    [`${base}_openings_manifest.json`]: manifestBytes,
    "README.txt": readmeBytes
  });

  postMessage({
    type: "result",
    fileName: `${base}_cs2rec_v4.zip`,
    zip,
    stats: {
      rounds: buildResult.manifest.rounds.length,
      entries: buildResult.entries.length,
      tickRows: allRows.length,
      parsedTicks,
      zipBytes: zip.byteLength
    }
  }, [zip.buffer]);
}

function parseTicksForSegment(bytes, ticks) {
  if (!ticks.length) {
    return [];
  }
  const wantedTicks = new Int32Array(ticks);
  try {
    return arrayOfObjects(wasm_bindgen.parseTicks(bytes, WANTED_PROPS, wantedTicks, null, false));
  } catch (error) {
    if (!WANTED_PROPS.includes("usercmd_subtick_moves")) {
      throw error;
    }
    const fallbackProps = WANTED_PROPS.filter(prop => prop !== "usercmd_subtick_moves");
    log(`Subtick parser failed for one segment; retrying without explicit subtick moves. ${(error && error.message) || error}`);
    return arrayOfObjects(wasm_bindgen.parseTicks(bytes, fallbackProps, wantedTicks, null, false));
  }
}

function buildDataset(sourceName, mapName, segments, rows, options) {
  const rowsBySegment = new Map();
  const segmentByTick = new Map();
  for (const segment of segments) {
    for (const tick of segment.ticks) {
      segmentByTick.set(tick, segment);
    }
  }

  for (const rawRow of rows) {
    const row = plain(rawRow);
    const tick = intValue(get(row, "tick"), -1);
    const segment = segmentByTick.get(tick);
    if (!segment) {
      continue;
    }
    let bucket = rowsBySegment.get(segment.freezeTick);
    if (!bucket) {
      bucket = [];
      rowsBySegment.set(segment.freezeTick, bucket);
    }
    bucket.push(row);
  }

  const bundlePath = `${sanitizeFileBase(`${mapName}_${stripExtension(sourceName)}`)}.cs2rec`;
  const entries = [];
  const rounds = [];
  let processedRows = 0;
  for (const segment of segments) {
    const roundRows = (rowsBySegment.get(segment.freezeTick) || [])
      .filter(isPlayableRow)
      .sort((left, right) => intValue(get(left, "tick"), 0) - intValue(get(right, "tick"), 0));
    if (!roundRows.length) {
      continue;
    }

    const activeRows = activeRoundRows(roundRows);
    const replayRows = activeRows.length ? activeRows : roundRows;
    const economyRows = sampleRoundRowsAtTick(replayRows, segment.economySampleTick);
    const freezeRows = sampleRoundRowsAtTick(roundRows, segment.freezeTick);
    const slotBySteamId = directSlotMap(freezeRows.length ? freezeRows : replayRows);
    const playerGroups = groupBy(replayRows, row => steamIdOf(row));
    const players = [];
    const roundKey = `${sanitizeFileBase(stripExtension(sourceName))}_r${segment.roundNumber}`;

    for (const [steamId, playerRowsRaw] of playerGroups.entries()) {
      if (!validSteamId(steamId)) {
        continue;
      }
      const playerRows = dedupeByTick(playerRowsRaw).sort((left, right) => intValue(get(left, "tick"), 0) - intValue(get(right, "tick"), 0));
      if (playerRows.length < 2) {
        continue;
      }
      const fallbackRows = freezeRows.filter(row => steamIdOf(row) === steamId);
      const baseline = samplePlayerRow(playerRows, segment.economySampleTick, fallbackRows);
      const teamNum = intValue(get(baseline, "team_num"), 0);
      if (teamNum !== 2 && teamNum !== 3) {
        continue;
      }

      const frames = inferFrameSequence(playerRows
        .map(row => directFrameFromRow(row, segment.freezeTick, options.tickrate))
        .filter(frame => intValue(frame.relativeTick, -1) >= 0), options.tickrate);
      if (frames.length < 2) {
        continue;
      }

      const subticksByTick = new Map();
      for (const row of playerRows) {
        const subticks = buildReplaySubticks(row);
        if (subticks.length) {
          subticksByTick.set(intValue(get(row, "tick"), 0), subticks);
        }
      }

      const safePlayer = sanitizeFileBase(`${teamNum}_${slotBySteamId.get(steamId) || 0}_${steamId}`);
      const recKey = `${roundKey}/${safePlayer}_round`;
      const route = {
        key: recKey,
        frames,
        subticksByTick,
        tickrate: options.tickrate,
        roundNumber: segment.roundNumber,
        teamNum,
        steamId,
        mapName,
        playerName: String(firstDefined(get(baseline, "player_name"), get(baseline, "name"), steamId) || steamId)
      };
      const entry = CS2RecWriter.buildRouteEntry(route, { downsample: options.downsample });
      if (!entry) {
        continue;
      }
      entries.push(entry);
      const info = CS2RecWriter.routeSegmentInfo(route, entry.weaponDefs);
      const retake = buildRetakeInfo(frames, segment);
      const playerPayload = {
        steamId,
        name: route.playerName,
        teamNum,
        slot: intValue(slotBySteamId.get(steamId), 0),
        startBalance: intValue(get(baseline, "balance"), 0),
        balance: intValue(get(baseline, "balance"), 0),
        economySampleRelativeTick: Math.max(0, segment.economySampleTick - segment.freezeTick),
        economySampleTime: roundFloat(Math.max(0, segment.economySampleTick - segment.freezeTick) / options.tickrate, 4),
        equipmentValue: intValue(firstDefined(get(baseline, "current_equip_value"), get(baseline, "round_start_equip_value")), 0),
        armorValue: intValue(get(baseline, "armor_value"), 0),
        hasHelmet: boolValue(get(baseline, "has_helmet")),
        hasDefuser: boolValue(get(baseline, "has_defuser")),
        inventory: normalizeInventory(get(baseline, "inventory")),
        inventoryDefIndexes: normalizeInventoryDefIndexes(get(baseline, "inventory_as_ids")),
        recPath: bundlePath,
        recKey,
        duration: roundFloat(info.duration, 4),
        firstWeaponDefIndex: info.firstWeaponDefIndex,
        preloadWeaponDefIndexes: info.preloadWeaponDefIndexes,
        startFrame: info.startFrame,
        endFrame: info.endFrame,
        grenades: []
      };
      if (retake) {
        Object.assign(playerPayload, retake);
      }
      players.push(playerPayload);
      processedRows += playerRows.length;
      progress("Building player routes", processedRows, rows.length, "rows");
    }

    if (players.length) {
      const roundPayload = {
        id: roundKey,
        demoPath: sourceName,
        roundNumber: segment.roundNumber,
        freezeEndTick: segment.freezeTick,
        economySampleRelativeTick: Math.max(0, segment.economySampleTick - segment.freezeTick),
        economySampleTime: roundFloat(Math.max(0, segment.economySampleTick - segment.freezeTick) / options.tickrate, 4),
        teamEconomies: directTeamEconomies(economyRows.length ? economyRows : freezeRows),
        players: players.sort((left, right) => (left.teamNum - right.teamNum) || (left.slot - right.slot))
      };
      if (segment.plantTick !== null) {
        roundPayload.plantRelativeTick = Math.max(0, segment.plantTick - segment.freezeTick);
        const plantPos = plantPosition(segment, replayRows, options);
        if (plantPos) {
          roundPayload.plantPos = plantPos;
        }
      }
      rounds.push(roundPayload);
    }
  }

  return {
    entries,
    manifest: {
      mapName,
      generatedAt: new Date().toISOString(),
      format: "cs2rec-v4-browser",
      rounds
    }
  };
}

function buildSegments(freezeTicks, roundEndEvents, plantEvents, options) {
  const roundEnds = sortedUniqueTicks(roundEndEvents);
  const maxRoundTicks = Math.max(1, options.maxRoundSeconds * options.tickrate);
  return freezeTicks.map((freezeTick, index) => {
    const nextFreezeTick = index + 1 < freezeTicks.length ? freezeTicks[index + 1] : null;
    let roundEndTick = firstTickInWindow(roundEnds, freezeTick, nextFreezeTick);
    if (roundEndTick === null) {
      roundEndTick = nextFreezeTick !== null ? nextFreezeTick - 1 : freezeTick + maxRoundTicks;
    }
    roundEndTick = Math.min(roundEndTick, freezeTick + maxRoundTicks);
    const plantEvent = firstEventInWindow(plantEvents, freezeTick, nextFreezeTick);
    const plantTick = plantEvent ? intValue(get(plantEvent, "tick"), -1) : null;
    const economySampleTick = Math.min(roundEndTick, freezeTick + Math.round(DEFAULT_ECONOMY_SAMPLE_SECONDS * options.tickrate));
    const tickSet = new Set();
    for (let tick = freezeTick; tick <= roundEndTick; tick++) {
      tickSet.add(tick);
    }
    tickSet.add(economySampleTick);
    return {
      roundNumber: index,
      freezeTick,
      nextFreezeTick,
      roundEndTick,
      plantTick,
      plantEvent,
      economySampleTick,
      tickrate: options.tickrate,
      ticks: [...tickSet].sort((left, right) => left - right)
    };
  });
}

function directFrameFromRow(row, freezeTick, tickrate) {
  const tick = intValue(get(row, "tick"), 0);
  const eyeAngles = vectorValue(get(row, "CCSPlayerPawn.m_angEyeAngles"), 3);
  const pitch = eyeAngles ? eyeAngles[0] : finiteNumber(firstDefined(get(row, "pitch"), get(row, "usercmd_viewangle_x")), 0);
  const yaw = eyeAngles ? eyeAngles[1] : finiteNumber(firstDefined(get(row, "yaw"), get(row, "usercmd_viewangle_y")), 0);
  const roll = eyeAngles ? eyeAngles[2] : 0;
  const buttons = intValue(firstDefined(get(row, "usercmd_buttonstate_1"), get(row, "buttons")), 0);
  const entityFlags = intValue(get(row, "CCSPlayerPawn.m_fFlags"), inferredEntityFlags(row));
  const moveType = intValue(firstDefined(get(row, "CCSPlayerPawn.m_MoveType"), get(row, "move_type")), 2);
  const actualMoveType = intValue(get(row, "CCSPlayerPawn.m_nActualMoveType"), moveType);
  const inferredDuck = (entityFlags & (1 << 1)) || (buttons & (1 << 2)) ? 1 : 0;
  const ladderNormal = vectorValue(get(row, "CCSPlayerPawn.CCSPlayer_MovementServices.m_vecLadderNormal"), 3) || [0, 0, 0];
  const activeWeapon = normalizeItem(get(row, "active_weapon_name"));
  const explicitDef = intOrNull(get(row, "item_def_idx"));
  return {
    tick,
    relativeTick: tick - freezeTick,
    timeSeconds: (tick - freezeTick) / tickrate,
    x: requiredNumber(get(row, "X"), "X"),
    y: requiredNumber(get(row, "Y"), "Y"),
    z: requiredNumber(get(row, "Z"), "Z"),
    velocityX: finiteNumber(get(row, "velocity_X"), NaN),
    velocityY: finiteNumber(get(row, "velocity_Y"), NaN),
    velocityZ: finiteNumber(get(row, "velocity_Z"), NaN),
    pitch,
    yaw,
    roll,
    entityFlags,
    moveType,
    actualMoveType,
    buttons,
    buttons1: intValue(get(row, "usercmd_buttonstate_2"), 0),
    buttons2: intValue(get(row, "usercmd_buttonstate_3"), 0),
    duckAmount: finiteNumber(get(row, "duck_amount"), inferredDuck ? 1 : 0),
    duckSpeed: finiteNumber(get(row, "duck_speed"), inferredDuck ? 8 : 0),
    ladderNormalX: finiteNumber(ladderNormal[0], 0),
    ladderNormalY: finiteNumber(ladderNormal[1], 0),
    ladderNormalZ: finiteNumber(ladderNormal[2], 0),
    ducked: intValue(get(row, "ducked"), inferredDuck),
    ducking: intValue(get(row, "ducking"), inferredDuck),
    desiresDuck: intValue(get(row, "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDesiresDuck"), inferredDuck),
    activeWeapon,
    activeWeaponDefIndex: explicitDef !== null ? CS2RecWriter.normalizeWeaponDefIndex(explicitDef) : CS2RecWriter.weaponDefIndex(activeWeapon)
  };
}

function inferFrameSequence(frames, tickrate) {
  const maxVelocity = 16384;
  for (let index = 0; index < frames.length; index++) {
    const frame = frames[index];
    for (const [axis, key] of [["x", "velocityX"], ["y", "velocityY"], ["z", "velocityZ"]]) {
      let velocity = finiteOrNull(frame[key]);
      if (velocity === null || Math.abs(velocity) > maxVelocity) {
        velocity = inferredAxisVelocity(frames, index, axis, tickrate);
      }
      frame[key] = Math.max(-maxVelocity, Math.min(maxVelocity, velocity));
    }
  }
  return frames;
}

function inferredAxisVelocity(frames, index, axis, tickrate) {
  const current = frames[index];
  for (const otherIndex of [index + 1, index - 1]) {
    if (otherIndex < 0 || otherIndex >= frames.length) {
      continue;
    }
    const other = frames[otherIndex];
    const deltaTicks = intValue(other.tick, otherIndex) - intValue(current.tick, index);
    if (deltaTicks === 0) {
      continue;
    }
    return (finiteNumber(other[axis], 0) - finiteNumber(current[axis], 0)) * tickrate / deltaTicks;
  }
  return 0;
}

function buildReplaySubticks(row) {
  const rawMoves = get(row, "usercmd_subtick_moves");
  if (Array.isArray(rawMoves) && rawMoves.length) {
    return rawMoves
      .map(move => ({
        when: clamp(finiteNumber(firstDefined(get(move, "when")), 0), 0, 0.999999),
        button: intValue(get(move, "button"), 0),
        pressed: finiteNumber(get(move, "pressed"), 0),
        analogForward: finiteNumber(firstDefined(get(move, "analog_forward_delta"), get(move, "analog_forward"), get(move, "analogForward")), 0),
        analogLeft: finiteNumber(firstDefined(get(move, "analog_left_delta"), get(move, "analog_left"), get(move, "analogLeft")), 0),
        pitchDelta: finiteNumber(firstDefined(get(move, "pitch_delta"), get(move, "pitchDelta")), 0),
        yawDelta: finiteNumber(firstDefined(get(move, "yaw_delta"), get(move, "yawDelta")), 0)
      }))
      .filter(move => !isNoopSubtick(move))
      .sort((left, right) => left.when - right.when);
  }

  const history = get(row, "usercmd_input_history");
  if (!Array.isArray(history) || !history.length) {
    return [];
  }
  let prevPitch = finiteNumber(firstDefined(get(row, "usercmd_viewangle_x"), get(row, "pitch")), 0);
  let prevYaw = finiteNumber(firstDefined(get(row, "usercmd_viewangle_y"), get(row, "yaw")), 0);
  const output = [];
  for (const entry of history.filter(item => item && typeof item === "object").sort((left, right) => subtickFraction(left) - subtickFraction(right))) {
    const pitch = finiteOrNull(firstDefined(get(entry, "x"), get(entry, "pitch")));
    const yaw = finiteOrNull(firstDefined(get(entry, "y"), get(entry, "yaw")));
    if (pitch === null || yaw === null) {
      continue;
    }
    const pitchDelta = pitch - prevPitch;
    const yawDelta = angleDelta(prevYaw, yaw);
    prevPitch = pitch;
    prevYaw = yaw;
    if (Math.abs(pitchDelta) < 0.000001 && Math.abs(yawDelta) < 0.000001) {
      continue;
    }
    output.push({
      when: subtickFraction(entry),
      button: 0,
      pressed: 0,
      analogForward: 0,
      analogLeft: 0,
      pitchDelta,
      yawDelta
    });
  }
  return output;
}

function isNoopSubtick(move) {
  return intValue(move.button, 0) === 0 &&
    Math.abs(finiteNumber(move.pressed, 0)) < 0.000001 &&
    Math.abs(finiteNumber(move.analogForward, 0)) < 0.000001 &&
    Math.abs(finiteNumber(move.analogLeft, 0)) < 0.000001 &&
    Math.abs(finiteNumber(move.pitchDelta, 0)) < 0.000001 &&
    Math.abs(finiteNumber(move.yawDelta, 0)) < 0.000001;
}

function directTeamEconomies(rows) {
  const byTeam = groupBy(rows, row => intValue(get(row, "team_num"), 0));
  const payloads = [];
  for (const [teamNum, teamRows] of byTeam.entries()) {
    if (teamNum !== 2 && teamNum !== 3) {
      continue;
    }
    const totalStartBalance = sum(teamRows, row => intValue(get(row, "balance"), 0));
    const totalEquipmentValue = sum(teamRows, row => intValue(firstDefined(get(row, "current_equip_value"), get(row, "round_start_equip_value")), 0));
    const totalArmorValue = sum(teamRows, row => intValue(get(row, "armor_value"), 0));
    const totalUtilityValue = sum(teamRows, row => inventoryUtilityValue(get(row, "inventory"), get(row, "inventory_as_ids")));
    const totalPrimaryValue = sum(teamRows, row => inventoryPrimaryValue(get(row, "inventory"), get(row, "inventory_as_ids")));
    payloads.push({
      teamNum,
      teamName: String(firstDefined(get(teamRows[0], "team_clan_name"), "") || ""),
      playerCount: teamRows.length,
      totalStartBalance,
      averageStartBalance: Math.round(totalStartBalance / Math.max(1, teamRows.length)),
      totalEquipmentValue,
      totalPrimaryValue,
      totalUtilityValue,
      totalArmorValue,
      totalCashEquipmentValue: totalStartBalance + totalEquipmentValue
    });
  }
  return payloads.sort((left, right) => left.teamNum - right.teamNum);
}

function buildRetakeInfo(frames, segment) {
  if (segment.plantTick === null) {
    return null;
  }
  const index = frames.findIndex(frame => intValue(frame.tick, 0) >= segment.plantTick);
  if (index < 0 || frames.length - index < 2) {
    return null;
  }
  const route = { frames: frames.slice(index) };
  const fakeDefs = [];
  for (let i = index; i < frames.length - 1; i++) {
    fakeDefs.push(frames[i].activeWeaponDefIndex === undefined || frames[i].activeWeaponDefIndex === null ? -1 : frames[i].activeWeaponDefIndex);
  }
  const info = CS2RecWriter.routeSegmentInfo(route, fakeDefs);
  return {
    retakeStartTickIndex: index,
    retakeStartRelativeTick: Math.max(0, segment.plantTick - segment.freezeTick),
    retakeStartTime: roundFloat((segment.plantTick - segment.freezeTick) / segment.tickrate, 4),
    retakeDuration: roundFloat(info.duration, 4),
    retakeStartFrame: info.startFrame,
    retakeEndFrame: info.endFrame
  };
}

function plantPosition(segment, rows, options) {
  const eventPos = [
    finiteOrNull(get(segment.plantEvent, "X")),
    finiteOrNull(get(segment.plantEvent, "Y")),
    finiteOrNull(get(segment.plantEvent, "Z"))
  ];
  if (eventPos.every(value => value !== null)) {
    return { x: roundFloat(eventPos[0]), y: roundFloat(eventPos[1]), z: roundFloat(eventPos[2]) };
  }

  const planterSteamId = steamIdOf(segment.plantEvent || {});
  let best = null;
  let bestDelta = Infinity;
  for (const row of rows) {
    if (validSteamId(planterSteamId) && steamIdOf(row) !== planterSteamId) {
      continue;
    }
    const tick = intValue(get(row, "tick"), -1);
    const delta = Math.abs(tick - segment.plantTick);
    if (delta < bestDelta) {
      best = row;
      bestDelta = delta;
    }
  }
  if (!best) {
    for (const row of rows) {
      const tick = intValue(get(row, "tick"), -1);
      const delta = Math.abs(tick - segment.plantTick);
      if (delta < bestDelta) {
        best = row;
        bestDelta = delta;
      }
    }
  }
  if (!best) {
    return null;
  }
  const frame = directFrameFromRow(best, segment.freezeTick, options.tickrate);
  return { x: roundFloat(frame.x), y: roundFloat(frame.y), z: roundFloat(frame.z) };
}

function parseEventSafe(bytes, eventName, wantedPlayerProps = []) {
  try {
    return arrayOfObjects(wasm_bindgen.parseEvent(bytes, eventName, wantedPlayerProps, []));
  } catch (error) {
    log(`Could not parse ${eventName}: ${(error && error.message) || error}`);
    return [];
  }
}

function sortedUniqueTicks(events) {
  return [...new Set(events
    .map(event => intValue(get(event, "tick"), -1))
    .filter(tick => tick >= 0))]
    .sort((left, right) => left - right);
}

function firstTickInWindow(ticks, start, endExclusive) {
  for (const tick of ticks) {
    if (tick >= start && (endExclusive === null || tick < endExclusive)) {
      return tick;
    }
  }
  return null;
}

function firstEventInWindow(events, start, endExclusive) {
  return events
    .filter(event => {
      const tick = intValue(get(event, "tick"), -1);
      return tick >= start && (endExclusive === null || tick < endExclusive);
    })
    .sort((left, right) => intValue(get(left, "tick"), 0) - intValue(get(right, "tick"), 0))[0] || null;
}

function activeRoundRows(rows) {
  const hasRoundProgress = rows.some(row => get(row, "round_in_progress") !== undefined);
  const hasFreeze = rows.some(row => get(row, "is_freeze_period") !== undefined);
  if (!hasRoundProgress && !hasFreeze) {
    return [];
  }
  return rows.filter(row => {
    const inProgress = hasRoundProgress ? boolValue(get(row, "round_in_progress")) : true;
    const notFreeze = hasFreeze ? !boolValue(get(row, "is_freeze_period")) : true;
    return inProgress && notFreeze;
  });
}

function sampleRoundRowsAtTick(rows, sampleTick) {
  const sampled = [];
  const bySteam = groupBy(rows, row => steamIdOf(row));
  for (const [steamId, playerRows] of bySteam.entries()) {
    if (!validSteamId(steamId)) {
      continue;
    }
    sampled.push(samplePlayerRow(playerRows, sampleTick));
  }
  return sampled.filter(Boolean);
}

function samplePlayerRow(rows, sampleTick, fallbackRows = []) {
  const sorted = [...rows].sort((left, right) => intValue(get(left, "tick"), 0) - intValue(get(right, "tick"), 0));
  return sorted.find(row => intValue(get(row, "tick"), 0) >= sampleTick) ||
    [...sorted].reverse().find(row => intValue(get(row, "tick"), 0) <= sampleTick) ||
    sorted[0] ||
    fallbackRows[0] ||
    null;
}

function directSlotMap(rows) {
  const ordered = [];
  const sorted = [...rows].sort((left, right) => {
    const teamDelta = intValue(get(left, "team_num"), 0) - intValue(get(right, "team_num"), 0);
    if (teamDelta !== 0) return teamDelta;
    return String(steamIdOf(left)).localeCompare(String(steamIdOf(right)));
  });
  for (const row of sorted) {
    const steamId = steamIdOf(row);
    if (validSteamId(steamId) && !ordered.includes(steamId)) {
      ordered.push(steamId);
    }
  }
  return new Map(ordered.map((steamId, index) => [steamId, index]));
}

function dedupeByTick(rows) {
  const seen = new Set();
  const output = [];
  for (const row of rows) {
    const tick = intValue(get(row, "tick"), -1);
    if (tick < 0 || seen.has(tick)) {
      continue;
    }
    seen.add(tick);
    output.push(row);
  }
  return output;
}

function isPlayableRow(row) {
  const teamNum = intValue(get(row, "team_num"), 0);
  return (teamNum === 2 || teamNum === 3) &&
    validSteamId(steamIdOf(row)) &&
    get(row, "is_alive") !== false &&
    get(row, "is_alive") !== 0;
}

function steamIdOf(row) {
  return String(firstDefined(get(row, "player_steamid"), get(row, "steamid"), "") || "");
}

function validSteamId(value) {
  const text = String(value || "");
  return !!text && text !== "0" && text !== "nan" && text !== "None" && text !== "undefined";
}

function inferredEntityFlags(row) {
  const airborne = firstDefined(get(row, "is_airborne"), get(row, "in_air"));
  if (airborne !== undefined) {
    return boolValue(airborne) ? 0 : 1;
  }
  return 1;
}

function normalizeInventory(raw) {
  if (!Array.isArray(raw)) {
    return [];
  }
  return raw.map(normalizeItem).filter(Boolean);
}

function normalizeInventoryDefIndexes(raw) {
  if (!Array.isArray(raw)) {
    return [];
  }
  return raw.map(value => intValue(value, -1)).filter(value => value >= 0);
}

const WEAPON_VALUES = new Map([
  [1, 700], [2, 300], [3, 500], [4, 200], [7, 2700], [8, 3300], [9, 4750], [10, 2050],
  [11, 5000], [13, 1800], [14, 5200], [16, 3100], [17, 1050], [19, 2350], [23, 1500],
  [24, 1200], [25, 2000], [26, 1400], [27, 1300], [28, 1700], [29, 1100], [30, 500],
  [32, 200], [33, 1500], [34, 1250], [35, 1050], [36, 300], [38, 5000], [39, 3000],
  [40, 1700], [43, 200], [44, 300], [45, 300], [46, 400], [47, 50], [48, 600],
  [60, 2900], [61, 200], [63, 500], [64, 600]
]);
const PRIMARY_DEFS = new Set([7, 8, 9, 10, 11, 13, 14, 16, 17, 19, 23, 24, 25, 26, 27, 28, 29, 33, 34, 35, 38, 39, 40, 60]);
const UTILITY_DEFS = new Set([43, 44, 45, 46, 47, 48]);

function inventoryPrimaryValue(inventory, inventoryDefs) {
  return inventoryDefIndexes(inventory, inventoryDefs)
    .filter(def => PRIMARY_DEFS.has(def))
    .reduce((sumValue, def) => sumValue + (WEAPON_VALUES.get(def) || 0), 0);
}

function inventoryUtilityValue(inventory, inventoryDefs) {
  return inventoryDefIndexes(inventory, inventoryDefs)
    .filter(def => UTILITY_DEFS.has(def))
    .reduce((sumValue, def) => sumValue + (WEAPON_VALUES.get(def) || 0), 0);
}

function inventoryDefIndexes(inventory, inventoryDefs) {
  const defs = normalizeInventoryDefIndexes(inventoryDefs);
  if (defs.length) {
    return defs.map(CS2RecWriter.normalizeWeaponDefIndex);
  }
  return normalizeInventory(inventory).map(name => CS2RecWriter.weaponDefIndex(name)).filter(def => def >= 0);
}

function normalizeItem(value) {
  const raw = String(value || "").trim().toLowerCase();
  if (!raw) return "";
  const alias = WEAPON_ALIASES.get(raw);
  if (alias) return alias;
  if (raw.startsWith("weapon_") || raw.startsWith("item_")) return raw;
  return `weapon_${raw.replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "")}`;
}

const WEAPON_ALIASES = new Map([
  ["ak-47", "weapon_ak47"],
  ["cz75-auto", "weapon_cz75a"],
  ["desert eagle", "weapon_deagle"],
  ["dual berettas", "weapon_elite"],
  ["five-seven", "weapon_fiveseven"],
  ["glock-18", "weapon_glock"],
  ["he grenade", "weapon_hegrenade"],
  ["high explosive grenade", "weapon_hegrenade"],
  ["incendiary grenade", "weapon_incgrenade"],
  ["m4a1-s", "weapon_m4a1_silencer"],
  ["m4a4", "weapon_m4a1"],
  ["mac-10", "weapon_mac10"],
  ["mag-7", "weapon_mag7"],
  ["mp5-sd", "weapon_mp5sd"],
  ["p2000", "weapon_hkp2000"],
  ["r8 revolver", "weapon_revolver"],
  ["sawed-off", "weapon_sawedoff"],
  ["scar-20", "weapon_scar20"],
  ["sg 553", "weapon_sg556"],
  ["smoke grenade", "weapon_smokegrenade"],
  ["ssg 08", "weapon_ssg08"],
  ["tec-9", "weapon_tec9"],
  ["ump-45", "weapon_ump45"],
  ["usp-s", "weapon_usp_silencer"]
]);

function normalizeOptions(options) {
  return {
    tickrate: clamp(intValue(options.tickrate, 64), 16, 256),
    downsample: clamp(intValue(options.downsample, 4), 1, 16),
    maxRoundSeconds: clamp(intValue(options.maxRoundSeconds, 115), 15, 300)
  };
}

function arrayOfObjects(value) {
  const normalized = plain(value);
  if (Array.isArray(normalized)) {
    return normalized.filter(item => item && typeof item === "object");
  }
  if (normalized && typeof normalized === "object") {
    const keys = Object.keys(normalized);
    const length = Math.max(0, ...keys.map(key => Array.isArray(normalized[key]) ? normalized[key].length : 0));
    const rows = [];
    for (let index = 0; index < length; index++) {
      const row = {};
      for (const key of keys) {
        if (Array.isArray(normalized[key])) {
          row[key] = normalized[key][index];
        }
      }
      rows.push(row);
    }
    return rows;
  }
  return [];
}

function plain(value) {
  if (value === null || value === undefined) {
    return value;
  }
  if (typeof value === "bigint") {
    return value.toString();
  }
  if (Array.isArray(value)) {
    return value.map(plain);
  }
  if (value instanceof Map) {
    return Object.fromEntries([...value.entries()].map(([key, item]) => [key, plain(item)]));
  }
  if (typeof value === "object") {
    const output = {};
    for (const [key, item] of Object.entries(value)) {
      output[key] = plain(item);
    }
    return output;
  }
  return value;
}

function get(object, key) {
  return object && typeof object === "object" ? object[key] : undefined;
}

function groupBy(values, keyFn) {
  const output = new Map();
  for (const value of values) {
    const key = keyFn(value);
    const bucket = output.get(key);
    if (bucket) {
      bucket.push(value);
    } else {
      output.set(key, [value]);
    }
  }
  return output;
}

function vectorValue(value, expected) {
  if (Array.isArray(value) && value.length >= expected) {
    return value.slice(0, expected).map(item => finiteNumber(item, 0));
  }
  if (value && typeof value === "object") {
    const candidates = [
      [value.x, value.y, value.z],
      [value.X, value.Y, value.Z],
      [value[0], value[1], value[2]]
    ];
    for (const candidate of candidates) {
      if (candidate.every(item => item !== undefined)) {
        return candidate.slice(0, expected).map(item => finiteNumber(item, 0));
      }
    }
  }
  return null;
}

function firstDefined(...values) {
  return values.find(value => value !== undefined && value !== null && value !== "");
}

function requiredNumber(value, label) {
  const parsed = finiteOrNull(value);
  if (parsed === null) {
    throw new Error(`Missing numeric ${label} in parsed tick row.`);
  }
  return parsed;
}

function finiteNumber(value, fallback) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function finiteOrNull(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function intValue(value, fallback) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? Math.trunc(parsed) : fallback;
}

function intOrNull(value) {
  if (value === undefined || value === null || value === "") return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? Math.trunc(parsed) : null;
}

function boolValue(value) {
  if (typeof value === "boolean") return value;
  if (typeof value === "string") return value === "true" || value === "1";
  return !!Number(value);
}

function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

function sum(values, valueFn) {
  return values.reduce((total, value) => total + valueFn(value), 0);
}

function subtickFraction(entry) {
  return clamp(finiteNumber(firstDefined(get(entry, "player_tick_fraction"), get(entry, "render_tick_fraction")), 0), 0, 0.999999);
}

function angleDelta(start, end) {
  return ((end - start) % 360 + 540) % 360 - 180;
}

function roundFloat(value, digits = 3) {
  const factor = 10 ** digits;
  return Math.round(finiteNumber(value, 0) * factor) / factor;
}

function unique(values) {
  return [...new Set(values)];
}

function sanitizeFileBase(value) {
  return String(value || "demo")
    .replace(/\.[^.]+$/, "")
    .replace(/[^a-zA-Z0-9._-]+/g, "_")
    .replace(/^_+|_+$/g, "") || "demo";
}

function stripExtension(value) {
  return String(value || "demo").replace(/\.[^.]+$/, "");
}

function normalizeMapName(value) {
  const text = String(value || "").trim();
  const match = text.match(/\b(de_[a-z0-9_]+|cs_[a-z0-9_]+)\b/i);
  return match ? match[1].toLowerCase() : text.toLowerCase();
}

function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MiB`;
}

function progress(phase, current, total, unit = "ticks") {
  postMessage({ type: "progress", phase, current, total, unit });
}

function log(message) {
  postMessage({ type: "log", message });
}
