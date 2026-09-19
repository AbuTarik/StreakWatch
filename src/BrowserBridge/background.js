const HOST = "com.streakwatch.bridge";

const MANAGED = new Map();          // channel -> tabId
const OPENING = new Set();          // channels currently being created
let lastState = null;
let port = null;
let reconnectTimer = null;
let reconcileRunning = false;

const PLAYER_RECOVERY = new Map();
const PLAYER_RECOVERY_MAX_ATTEMPTS = 3;
const PLAYER_RECOVERY_RETRY_MS = 8000;
const PLAYER_RECOVERY_COOLDOWN_MS = 60000;
const PLAYBACK_STATE = new Map();   // tabId -> playback tracking
const REPORTS = new Map();             // channel -> app-visible status
const PLAYBACK_STALL_MS = 75000;
const PLAYBACK_START_RETRY_MS = 12000;
const PLAYBACK_NO_VIDEO_RELOAD_MS = 90000;
const PLAYBACK_RELOAD_COOLDOWN_MS = 180000;
const PLAYBACK_MAX_RELOADS = 1;
const PLAYBACK_STARTUP_GRACE_MS = 45000;
const NETWORK_RECOVERY_GRACE_MS = 45000;
const ACTIVATION_VERIFY_TIMEOUT_MS = 25000;
const ACTIVATION_SAMPLE_MS = 1800;
let reconcileRequested = false;
let activationQueueRunning = false;
const ACTIVATION_QUEUE = [];
const ACTIVATION_IN_FLIGHT = new Map(); // channel -> tabId currently being primed
const ACTIVATION_COOLDOWN_UNTIL = new Map(); // channel -> retry-after timestamp
const ACTIVATION_FAILURE_COOLDOWN_MS = 90000;
let lastNetworkRecoveryGeneration = null;
let lastNetworkRecoveryReloadAt = 0;
let currentSessionId = null;
let networkGraceUntil = 0;
let savedActiveTab = null;
const INTENTIONAL_CLOSE = new Set();
const SUPPRESS_STREAM = new Map();
const REOPEN_TIMERS = new Map();
const NETWORK_VERIFY = new Map();
const NETWORK_VERIFY_MAX = 2;
const DEDUPE_RUNNING = new Set();

const QUALITY_STATE = new Map();    // tabId -> quality state for current page load
const LOW_DATA_WATCH = new Map();   // channel -> { streamId, verifiedMs, lastSampleAt }
const LOW_DATA_DONE = new Map();    // channel -> completed streamId
const QUALITY_RETRY_MS = 15000;
const QUALITY_CHANGE_GRACE_MS = 30000;

function log(...args) {
  console.log("[StreakWatch Bridge]", ...args);
}

function setReport(
  channel,
  status,
  tabId = null,
  failureCount = 0,
  lastSuccessfulPlaybackUtc = undefined,
  telemetry = undefined
) {
  const previous = REPORTS.get(channel) || {};
  const t = telemetry || {};

  REPORTS.set(channel, {
    Status: status,
    TabId: tabId,
    FailureCount: failureCount,
    LastSuccessfulPlaybackUtc:
      lastSuccessfulPlaybackUtc !== undefined
        ? lastSuccessfulPlaybackUtc
        : (previous.LastSuccessfulPlaybackUtc || null),

    HasVideo: t.HasVideo !== undefined ? t.HasVideo : (previous.HasVideo ?? null),
    Paused: t.Paused !== undefined ? t.Paused : (previous.Paused ?? null),
    ReadyState: t.ReadyState !== undefined ? t.ReadyState : (previous.ReadyState ?? null),
    CurrentTime: t.CurrentTime !== undefined ? t.CurrentTime : (previous.CurrentTime ?? null),
    Progressing: t.Progressing !== undefined ? t.Progressing : (previous.Progressing ?? null),
    LastProgressUtc:
      t.LastProgressUtc !== undefined
        ? t.LastProgressUtc
        : (previous.LastProgressUtc || null),
    RecoveryAction:
      t.RecoveryAction !== undefined
        ? t.RecoveryAction
        : (previous.RecoveryAction || null),

    LastEventType: previous.LastEventType || null,
    LastEventUtc: previous.LastEventUtc || null,
    LastEventDetail: previous.LastEventDetail || null
  });
}

function setBrowserEvent(channel, type, detail = "") {
  const previous = REPORTS.get(channel) || {};
  REPORTS.set(channel, {
    ...previous,
    LastEventType: type,
    LastEventUtc: new Date().toISOString(),
    LastEventDetail: detail
  });
  log(`${type}:`, channel, detail);
}

function telemetryFrom(status, progressing, state, recoveryAction = null) {
  return {
    HasVideo: !!status.hasVideo,
    Paused: !!status.paused,
    ReadyState: Number(status.readyState || 0),
    CurrentTime: Number(status.currentTime || 0),
    Progressing: !!progressing,
    LastProgressUtc: state.lastProgressAt ? new Date(state.lastProgressAt).toISOString() : null,
    RecoveryAction: recoveryAction
  };
}

function currentStreamId(channel) {
  return String(lastState?.LiveStreamIds?.[channel] || "");
}
function isSuppressedForCurrentStream(channel) {
  const blocked = SUPPRESS_STREAM.get(channel);
  const current = currentStreamId(channel);
  if (!blocked) return false;
  if (current && blocked !== current) { SUPPRESS_STREAM.delete(channel); return false; }
  return current !== "" && blocked === current;
}
function scheduleSingleReopen(channel, delayMs = 1800) {
  if (REOPEN_TIMERS.has(channel)) return;
  const timer = setTimeout(async () => {
    REOPEN_TIMERS.delete(channel);
    const live = (lastState?.LiveChannels || []).map(x => String(x).toLowerCase());
    if (!lastState?.AppRunning || !live.includes(channel) || isSuppressedForCurrentStream(channel) || isLowDataDone(channel)) return;
    await reconcile();
  }, delayMs);
  REOPEN_TIMERS.set(channel, timer);
}
function cancelReopen(channel) {
  const t = REOPEN_TIMERS.get(channel); if (t) clearTimeout(t);
  REOPEN_TIMERS.delete(channel);
}


function bridgeNetworkHealthy() {
  return lastState?.NetworkHealthy !== false;
}

function inNetworkGrace() {
  return !bridgeNetworkHealthy() || Date.now() < networkGraceUntil;
}

function resetPlaybackTrackingForSession(reason = "new-session") {
  PLAYBACK_STATE.clear();
  QUALITY_STATE.clear();
  PLAYER_RECOVERY.clear();
  LOW_DATA_WATCH.clear();
  LOW_DATA_DONE.clear();

  for (const item of NETWORK_VERIFY.values()) {
    if (item?.timer) clearTimeout(item.timer);
  }
  NETWORK_VERIFY.clear();

  ACTIVATION_QUEUE.splice(0, ACTIVATION_QUEUE.length);
  ACTIVATION_IN_FLIGHT.clear();
  ACTIVATION_COOLDOWN_UNTIL.clear();
  activationQueueRunning = false;
  networkGraceUntil = Date.now() + PLAYBACK_STARTUP_GRACE_MS;
  log("Playback tracking reset:", reason);
}

function handleSessionState(message) {
  const incoming = String(message?.SessionId || "");

  if (!incoming) return;

  if (currentSessionId === null) {
    currentSessionId = incoming;
    resetPlaybackTrackingForSession("bridge-session-adopted");
    return;
  }

  if (incoming !== currentSessionId) {
    currentSessionId = incoming;
    resetPlaybackTrackingForSession("desktop-session-changed");
  }
}

async function playbackActuallyProgresses(tabId, waitMs = 1400) {
  const first = await getPlaybackStatus(tabId, true);
  if (!first.hasVideo || first.ended) return { ok: false, status: first };

  await new Promise(resolve => setTimeout(resolve, waitMs));
  const second = await getPlaybackStatus(tabId, false);

  const dt = Number(second.currentTime || 0) - Number(first.currentTime || 0);
  const df = Number(second.totalVideoFrames || 0) - Number(first.totalVideoFrames || 0);

  return {
    ok: second.hasVideo && !second.ended && !second.paused &&
        second.readyState >= 2 && (dt > 0.35 || df >= 3),
    status: second
  };
}

async function primeManagedTab(channel, tabId) {
  // Low Data target is terminal for the current broadcast. Never re-prime it.
  if (isLowDataDone(channel)) return true;
  const started = Date.now();
  setReport(channel, "Priming playback", tabId, 0);

  while (Date.now() - started < ACTIVATION_VERIFY_TIMEOUT_MS) {
    if (!bridgeNetworkHealthy()) {
      setReport(channel, "Waiting for network", tabId, 0);
      return false;
    }

    try {
      const tab = await browser.tabs.get(tabId);
      if (channelFromUrl(tab.url || "") !== channel) return false;

      const check = await playbackActuallyProgresses(tabId, ACTIVATION_SAMPLE_MS);
      if (check.ok) {
        const now = Date.now();
        PLAYBACK_STATE.set(tabId, {
          lastTime: Number(check.status.currentTime || 0),
          lastFrames: Number(check.status.totalVideoFrames || 0),
          lastProgressAt: now,
          firstSeenAt: now,
          startupGraceUntil: now + PLAYBACK_STARTUP_GRACE_MS,
          noVideoSince: null,
          problemSince: null,
          consecutiveBadSamples: 0,
          startAttempts: 0,
          lastStartAttemptAt: 0,
          reloadAttempts: 0,
          lastReloadAt: 0
        });

        setReport(
          channel, "Playing", tabId, 0, new Date().toISOString(),
          telemetryFrom(check.status, true, PLAYBACK_STATE.get(tabId), null)
        );

        // Quality is applied only after we have first proven that this tab
        // really plays. The user may change it later and we will leave it alone.
        await ensureLowestQuality(channel, tabId, true);
        if (lowDataEnabled()) {
          LOW_DATA_WATCH.set(channel, {
            streamId: streamKey(channel),
            verifiedMs: LOW_DATA_WATCH.get(channel)?.verifiedMs || 0,
            lastSampleAt: Date.now()
          });
        }
        return true;
      }
    } catch {}

    await new Promise(resolve => setTimeout(resolve, 1200));
  }

  // The target may have been reached while this activation was queued/running.
  if (isLowDataDone(channel)) return true;
  setReport(channel, "Playback needs attention", tabId, 0);
  setBrowserEvent(channel, "PLAYBACK-START-TIMEOUT", `could not confirm playback within ${Math.round(ACTIVATION_VERIFY_TIMEOUT_MS/1000)}s`);
  return false;
}

function queueActivation(channel, tabId) {
  if (isLowDataDone(channel)) return;
  const normalized = String(channel).toLowerCase();
  if (ACTIVATION_IN_FLIGHT.get(normalized) === tabId) return;
  if (Number(ACTIVATION_COOLDOWN_UNTIL.get(normalized) || 0) > Date.now()) return;
  if (!ACTIVATION_QUEUE.some(x => x.channel === normalized))
    ACTIVATION_QUEUE.push({ channel: normalized, tabId });

  runActivationQueue();
}

async function runActivationQueue() {
  if (activationQueueRunning) return;
  activationQueueRunning = true;

  try {
    // Preserve what the user was doing before StreakWatch briefly foregrounds
    // each Twitch tab. This is especially important when several channels go
    // live at once.
    if (savedActiveTab == null) {
      try {
        const active = await browser.tabs.query({ active: true, currentWindow: true });
        savedActiveTab = active?.[0]?.id ?? null;
      } catch {}
    }

    while (ACTIVATION_QUEUE.length > 0) {
      const item = ACTIVATION_QUEUE.shift();
      if (isLowDataDone(item.channel)) continue;

      try {
        const tab = await browser.tabs.get(item.tabId);
        if (channelFromUrl(tab.url || "") !== item.channel) continue;

        await browser.tabs.update(item.tabId, { active: true });
        await browser.windows.update(tab.windowId, { focused: true });

        // Firefox/Twitch often fails to initialize several background streams
        // opened simultaneously. Prime each LIVE channel one-by-one and do not
        // move to the next until real clock/frame progress is observed or the
        // bounded startup timeout expires.
        ACTIVATION_IN_FLIGHT.set(item.channel, item.tabId);
        const ok = await primeManagedTab(item.channel, item.tabId);
        if (ok) ACTIVATION_COOLDOWN_UNTIL.delete(item.channel);
        else ACTIVATION_COOLDOWN_UNTIL.set(item.channel, Date.now() + ACTIVATION_FAILURE_COOLDOWN_MS);
      } catch (e) {
        ACTIVATION_COOLDOWN_UNTIL.set(item.channel, Date.now() + ACTIVATION_FAILURE_COOLDOWN_MS);
        log("Activation queue item failed:", item.channel, item.tabId, e);
      } finally {
        if (ACTIVATION_IN_FLIGHT.get(item.channel) === item.tabId)
          ACTIVATION_IN_FLIGHT.delete(item.channel);
      }
    }
  } finally {
    if (savedActiveTab != null) {
      try { await browser.tabs.update(savedActiveTab, { active: true }); } catch {}
    }
    savedActiveTab = null;
    activationQueueRunning = false;
  }
}

async function verifyAfterNetworkRecovery(channel, tabId, attempt = 0) {
  try {
    if (!bridgeNetworkHealthy()) return;

    const tab = await browser.tabs.get(tabId);
    if (channelFromUrl(tab.url || "") !== channel) return;

    const check = await playbackActuallyProgresses(tabId, 1600);
    if (check.ok) {
      setReport(channel, "Playing", tabId, 0, new Date().toISOString());
      NETWORK_VERIFY.delete(channel);
      return;
    }

    // One conservative retry only. Do not create reload loops.
    if (attempt >= 1) {
      setReport(channel, "Playback needs attention", tabId, 1);
      NETWORK_VERIFY.delete(channel);
      return;
    }

    await browser.tabs.reload(tabId, { bypassCache: false });
    const timer = setTimeout(
      () => verifyAfterNetworkRecovery(channel, tabId, attempt + 1),
      15000
    );
    NETWORK_VERIFY.set(channel, { attempts: attempt + 1, timer });
  } catch {
    NETWORK_VERIFY.delete(channel);
  }
}

async function reloadManagedAfterNetworkRestore(reason = "network-restored") {
  const now = Date.now();

  // Debounce duplicate desktop/browser "online" signals for one physical
  // reconnect and give Twitch/Firefox time to restore networking.
  if (now - lastNetworkRecoveryReloadAt < 30000) return;
  lastNetworkRecoveryReloadAt = now;
  networkGraceUntil = now + NETWORK_RECOVERY_GRACE_MS;

  const wanted = (lastState?.LiveChannels || []).map(c => String(c).toLowerCase());
  log(`Network recovery: ${reason}; scheduling LIVE tabs for verified re-prime`);

  setTimeout(async () => {
    if (!bridgeNetworkHealthy()) return;

    for (const channel of wanted) {
      if (isSuppressedForCurrentStream(channel) || isLowDataDone(channel)) continue;

      let tabId = MANAGED.get(channel);
      if (tabId == null) {
        const existing = await findTwitchTabsByChannel(channel);
        if (existing.length) {
          tabId = existing[0].id;
          MANAGED.set(channel, tabId);
        }
      }

      if (tabId == null) continue;

      // Do not reload every LIVE tab at once. First try to resume and verify.
      const check = await playbackActuallyProgresses(tabId, 1600);
      if (check.ok) {
        setReport(channel, "Playing", tabId, 0, new Date().toISOString());
        continue;
      }

      queueActivation(channel, tabId);
    }
  }, NETWORK_RECOVERY_GRACE_MS);
}

async function handleNetworkRecoverySignal(message) {
  const generation = Number(message?.NetworkRecoveryGeneration || 0);

  // On extension startup, adopt the current generation without reloading.
  if (lastNetworkRecoveryGeneration === null) {
    lastNetworkRecoveryGeneration = generation;
    return;
  }

  if (generation > lastNetworkRecoveryGeneration) {
    lastNetworkRecoveryGeneration = generation;
    networkGraceUntil = Date.now() + NETWORK_RECOVERY_GRACE_MS;
    await reloadManagedAfterNetworkRestore(`app-generation-${generation}`);
  }
}

function channelFromUrl(url) {
  try {
    const u = new URL(url);
    if (!u.hostname.endsWith("twitch.tv")) return null;

    const parts = u.pathname.split("/").filter(Boolean);
    if (parts.length < 1) return null;

    const first = parts[0].toLowerCase();
    if ([
      "directory", "videos", "downloads", "settings",
      "subscriptions", "wallet", "inventory"
    ].includes(first)) {
      return null;
    }

    return first;
  } catch {
    return null;
  }
}

async function twitchTabs() {
  return browser.tabs.query({ url: ["*://*.twitch.tv/*"] });
}

function isStreakWatchOwnedUrl(url) {
  try {
    const u = new URL(url || "");
    return u.searchParams.get("streakwatch_managed") === "1";
  } catch {
    return false;
  }
}

async function findTwitchTabsByChannel(channel) {
  const tabs = await twitchTabs();
  const normalized = channel.toLowerCase();
  return tabs.filter(t => channelFromUrl(t.url || "") === normalized);
}

async function findOwnedTwitchTabsByChannel(channel) {
  const tabs = await findTwitchTabsByChannel(channel);
  const managedId = MANAGED.get(channel.toLowerCase());
  return tabs.filter(t => t.id === managedId || isStreakWatchOwnedUrl(t.url || ""));
}

async function dedupeChannelTabs(channel, preferredTabId = null) {
  const normalized = channel.toLowerCase();
  if (DEDUPE_RUNNING.has(normalized)) return preferredTabId;

  DEDUPE_RUNNING.add(normalized);
  try {
    const tabs = await findOwnedTwitchTabsByChannel(normalized);
    if (tabs.length <= 1) {
      const only = tabs[0];
      if (only?.id != null) MANAGED.set(normalized, only.id);
      return only?.id ?? preferredTabId;
    }

    let keep = null;

    if (preferredTabId != null)
      keep = tabs.find(t => t.id === preferredTabId) || null;

    if (!keep) {
      const managedId = MANAGED.get(normalized);
      if (managedId != null)
        keep = tabs.find(t => t.id === managedId) || null;
    }

    if (!keep)
      keep = tabs.find(t => t.active) || tabs[0];

    MANAGED.set(normalized, keep.id);

    const extras = tabs.filter(t => t.id !== keep.id);
    for (const extra of extras) {
      try {
        // Close only tabs positively owned by StreakWatch. Manual Twitch tabs are never touched.
        // Mark intentional so onRemoved does not trigger a reopen.
        INTENTIONAL_CLOSE.add(extra.id);
        await browser.tabs.remove(extra.id);
        log("Duplicate Twitch tab closed:", normalized, extra.id, "kept", keep.id);
      } catch (e) {
        log("Failed to close duplicate tab:", normalized, extra.id, e);
      }
    }

    return keep.id;
  } finally {
    DEDUPE_RUNNING.delete(normalized);
  }
}

async function muteManagedTab(tabId, channel = null) {
  try {
    const shouldMute =
      channel == null ? true : (lastState?.MuteChannels?.[channel] !== false);

    // Tab-level mute follows the per-channel setting.
    await browser.tabs.update(tabId, { muted: shouldMute });
  } catch {}
}

async function managedTabStillExists(channel) {
  const tabId = MANAGED.get(channel);
  if (tabId == null) return false;

  try {
    const tab = await browser.tabs.get(tabId);
    if (channelFromUrl(tab.url || "") === channel) {
      await muteManagedTab(tabId, channel);
      return true;
    }
  } catch {}

  MANAGED.delete(channel);
  return false;
}

async function ensureChannel(channel) {
  const normalized = channel.toLowerCase();
  const previousReport = REPORTS.get(normalized) || {};
  const recoveringMissingTab =
    String(previousReport.Status || "").startsWith("Tab closed") ||
    String(previousReport.Status || "").startsWith("Tab missing");

  if (isSuppressedForCurrentStream(normalized)) {
    setReport(normalized, "Raid/redirect - not reopening", null, 0);
    return null;
  }

  // Hard single-flight guard. Take ownership BEFORE any async lookup so two
  // recovery/reconcile paths can never both decide that the tab is missing.
  if (OPENING.has(normalized)) return MANAGED.get(normalized) ?? null;
  OPENING.add(normalized);

  try {
    if (!REPORTS.has(normalized)) setReport(normalized, "Opening");

    if (await managedTabStillExists(normalized))
      return MANAGED.get(normalized);

    // Adopt/dedupe only tabs positively marked as StreakWatch-owned.
    // A user's manually opened Twitch tab is deliberately ignored.
    const existingOwned = await findOwnedTwitchTabsByChannel(normalized);
    if (existingOwned.length > 0) {
      const keepId = await dedupeChannelTabs(normalized, existingOwned[0].id);
      const tab = await browser.tabs.get(keepId);
      MANAGED.set(normalized, tab.id);
      setReport(normalized, "Starting", tab.id);
      await muteManagedTab(tab.id, normalized);
      queueActivation(normalized, tab.id);
      if (recoveringMissingTab)
        setBrowserEvent(normalized, "TAB-RECOVERED", `adopted owned tab ${tab.id}`);
      return tab.id;
    }

    // One last owned-tab check immediately before creation.
    const secondCheck = await findOwnedTwitchTabsByChannel(normalized);
    if (secondCheck.length > 0) {
      const keepId = await dedupeChannelTabs(normalized, secondCheck[0].id);
      const tab = await browser.tabs.get(keepId);
      MANAGED.set(normalized, tab.id);
      setReport(normalized, "Starting", tab.id);
      await muteManagedTab(tab.id, normalized);
      queueActivation(normalized, tab.id);
      return tab.id;
    }

    const tab = await browser.tabs.create({
      url: `https://www.twitch.tv/${encodeURIComponent(normalized)}?streakwatch_managed=1`,
      active: false
    });

    MANAGED.set(normalized, tab.id);
    setReport(normalized, "Starting", tab.id);
    await muteManagedTab(tab.id, normalized);
    log("Opened managed tab:", normalized, tab.id);

    // Defensive sweep for an unexpected browser/startup race. Only owned
    // StreakWatch copies are eligible for cleanup.
    setTimeout(() => dedupeChannelTabs(normalized, tab.id), 1200);
    queueActivation(normalized, tab.id);
    return tab.id;
  } catch (e) {
    log("Failed to create/adopt tab", normalized, e);
    setReport(normalized, "Open failed", null, 1);
    return null;
  } finally {
    OPENING.delete(normalized);
  }
}



function lowDataEnabled() {
  return lastState?.LowDataMode === true;
}

function lowDataLimit() {
  return Math.max(1, Math.min(4, Number(lastState?.MaxSimultaneousStreams || 1)));
}

function lowDataTargetMs() {
  return Math.max(300000, Math.min(3600000, Number(lastState?.LowDataTargetSeconds || 600) * 1000));
}

function streamKey(channel) {
  return String(lastState?.LiveStreamIds?.[channel] || "");
}

function pruneLowDataState(liveSet) {
  for (const [channel, doneStream] of [...LOW_DATA_DONE.entries()]) {
    const current = streamKey(channel);
    if (!liveSet.has(channel) || (current && doneStream && current !== doneStream)) {
      LOW_DATA_DONE.delete(channel);
      LOW_DATA_WATCH.delete(channel);
    }
  }
  for (const channel of [...LOW_DATA_WATCH.keys()]) {
    if (!liveSet.has(channel)) LOW_DATA_WATCH.delete(channel);
  }
}

function isLowDataDone(channel) {
  const done = LOW_DATA_DONE.get(channel);
  if (!done) return false;
  const current = streamKey(channel);
  return !current || current === done;
}

async function finishLowDataSession(channel, tabId) {
  const current = streamKey(channel) || `session:${currentSessionId || "unknown"}:${channel}`;
  LOW_DATA_DONE.set(channel, current);
  LOW_DATA_WATCH.delete(channel);
  cancelReopen(channel);
  ACTIVATION_IN_FLIGHT.delete(channel);
  ACTIVATION_COOLDOWN_UNTIL.delete(channel);
  for (let i = ACTIVATION_QUEUE.length - 1; i >= 0; i--) {
    if (ACTIVATION_QUEUE[i].channel === channel) ACTIVATION_QUEUE.splice(i, 1);
  }

  MANAGED.delete(channel);
  OPENING.delete(channel);
  PLAYBACK_STATE.delete(tabId);
  QUALITY_STATE.delete(tabId);
  PLAYER_RECOVERY.delete(tabId);

  setReport(channel, "Low Data target reached", null, 0);
  setBrowserEvent(
    channel,
    "LOW-DATA-TARGET",
    `verified playback target reached (${Math.round(lowDataTargetMs()/1000)}s); managed tab closed`
  );

  if (lastState?.CloseAfterLowDataTarget !== false) {
    try {
      const tab = await browser.tabs.get(tabId);
      if (channelFromUrl(tab.url || "") === channel) {
        INTENTIONAL_CLOSE.add(tabId);
        await browser.tabs.remove(tabId);
      }
    } catch {}
  }

  // Immediately allow the next waiting LIVE channel into the limited slot.
  reconcile();
}

async function recordLowDataProgress(channel, tabId) {
  if (!lowDataEnabled() || lastState?.MonitorOnly === true || isLowDataDone(channel)) return;

  const now = Date.now();
  const current = streamKey(channel);
  let s = LOW_DATA_WATCH.get(channel);

  if (!s || (current && s.streamId && s.streamId !== current)) {
    s = { streamId: current, verifiedMs: 0, lastSampleAt: now };
  }

  // Count only bounded intervals between samples that were independently
  // verified as actually progressing. Sleep/offline gaps never count.
  const delta = Math.max(0, Math.min(15000, now - Number(s.lastSampleAt || now)));
  s.verifiedMs += delta;
  s.lastSampleAt = now;
  s.streamId = current || s.streamId;
  LOW_DATA_WATCH.set(channel, s);

  if (s.verifiedMs >= lowDataTargetMs()) {
    await finishLowDataSession(channel, tabId);
  }
}

async function doReconcile() {
  const state = lastState;
  if (!state || !state.AppRunning) return;

  const liveList = (state.LiveChannels || []).map(c => String(c).toLowerCase());
  const recoveryDeadlines = state.RecoveryDeadlinesUtc || {};
  const now = Date.now();
  const recoveryLive = liveList.filter(channel => {
    const deadline = Date.parse(recoveryDeadlines[channel] || "");
    return Number.isFinite(deadline) && deadline > now;
  });
  const normalLive = liveList.filter(channel => !recoveryLive.includes(channel));
  let orderedWanted = [...recoveryLive, ...normalLive];
  const allLive = new Set(orderedWanted);
  pruneLowDataState(allLive);

  // Monitor-only keeps detection/Discord alive but never opens Twitch.
  if (state.MonitorOnly === true) {
    orderedWanted = [];
  } else if (state.LowDataMode === true) {
    // Recovery-deadline channels are already first. Skip broadcasts that
    // completed this low-data session and admit only N active streams.
    const eligible = orderedWanted.filter(channel => !isLowDataDone(channel));

    const currentlyManaged = eligible.filter(channel => MANAGED.has(channel));
    const waiting = eligible.filter(channel => !MANAGED.has(channel));
    const limit = lowDataLimit();

    orderedWanted = [...currentlyManaged, ...waiting].slice(0, limit);
  }

  const wanted = new Set(orderedWanted);

  if (state.LowDataMode === true && state.MonitorOnly !== true) {
    for (const channel of allLive) {
      if (!wanted.has(channel) && !isLowDataDone(channel)) {
        setReport(channel, "Low Data queue - waiting", null, 0);
      }
    }
  }
  for (const [channel, blocked] of [...SUPPRESS_STREAM.entries()]) {
    const current = currentStreamId(channel);
    if (!wanted.has(channel) || (current && current !== blocked)) SUPPRESS_STREAM.delete(channel);
  }

  for (const channel of [...MANAGED.keys()]) {
    if (!wanted.has(channel)) {
      SUPPRESS_STREAM.delete(channel);
      cancelReopen(channel);
      const tabId = MANAGED.get(channel);

      // Channel was previously LIVE/managed and is no longer in StreakWatch's
      // LIVE list. Close only the exact tab that this extension manages.
      // Never close unrelated Twitch tabs.
      MANAGED.delete(channel);
      OPENING.delete(channel);

      if (tabId != null) {
        PLAYER_RECOVERY.delete(tabId);
        PLAYBACK_STATE.delete(tabId);

        const shouldClose =
          state.CloseWhenOffline?.[channel] !== false;

        if (shouldClose) {
          try {
            const tab = await browser.tabs.get(tabId);
            if (channelFromUrl(tab.url || "") === channel) {
              INTENTIONAL_CLOSE.add(tabId);
              cancelReopen(channel);
              await browser.tabs.remove(tabId);
              log("Channel OFFLINE - closed managed tab:", channel, tabId);
            }
          } catch {
            // Tab was already closed.
          }
        } else {
          log("Channel OFFLINE - leaving tab open by policy:", channel, tabId);
        }
      }

      setReport(channel, "Offline", null, 0);
    }
  }

  for (const channel of [...OPENING]) {
    if (!wanted.has(channel)) {
      OPENING.delete(channel);
    }
  }

  if (!state.ReopenClosedLiveTabs) return;

  // Create/adopt ONLY the channels selected by orderedWanted. This is
  // critical in Low Data Mode: opening recoveryLive/normalLive directly here
  // bypassed MaxSimultaneousStreams, so an unselected LIVE channel could be
  // opened and then closed by the next reconcile forever.
  for (const channel of orderedWanted)
    await ensureChannel(channel);

  for (const channel of orderedWanted) {
    let tabId = MANAGED.get(channel);
    tabId = await dedupeChannelTabs(channel, tabId);
    if (tabId != null)
      await muteManagedTab(tabId, channel);
  }
}

async function reconcile() {
  if (reconcileRunning) {
    reconcileRequested = true;
    return;
  }

  reconcileRunning = true;
  try {
    do {
      reconcileRequested = false;
      await doReconcile();
    } while (reconcileRequested);
  } finally {
    reconcileRunning = false;
  }
}



async function inspectQualityOptions(tabId) {
  try {
    const result = await browser.tabs.executeScript(tabId, {
      code: `(() => {
        const visible = el => {
          if (!el) return false;
          const s = getComputedStyle(el);
          const r = el.getBoundingClientRect();
          return s.display !== "none" && s.visibility !== "hidden" &&
                 r.width > 0 && r.height > 0;
        };

        return Array.from(document.querySelectorAll(
          '[role="menuitemradio"], [role="radio"], button, [data-a-target*="quality"]'
        ))
        .filter(visible)
        .map(el => ({
          text: (el.innerText || el.textContent || "").trim(),
          checked:
            el.getAttribute("aria-checked") === "true" ||
            el.getAttribute("data-selected") === "true"
        }))
        .filter(x => /(\\d{3,4})p(?:\\d+)?/i.test(x.text));
      })();`
    });

    return Array.isArray(result?.[0]) ? result[0] : [];
  } catch {
    return [];
  }
}

async function clickPlayerSettings(tabId) {
  try {
    const result = await browser.tabs.executeScript(tabId, {
      code: `(() => {
        const selectors = [
          '[data-a-target="player-settings-button"]',
          'button[aria-label*="Settings" i]',
          'button[aria-label*="إعداد" i]'
        ];

        for (const selector of selectors) {
          const el = document.querySelector(selector);
          if (el) { el.click(); return true; }
        }

        return false;
      })();`
    });
    return !!result?.[0];
  } catch {
    return false;
  }
}

async function clickQualityMenuItem(tabId) {
  try {
    const result = await browser.tabs.executeScript(tabId, {
      code: `(() => {
        const visible = el => {
          if (!el) return false;
          const s = getComputedStyle(el);
          const r = el.getBoundingClientRect();
          return s.display !== "none" && s.visibility !== "hidden" &&
                 r.width > 0 && r.height > 0;
        };

        const direct = document.querySelector(
          '[data-a-target="player-settings-menu-item-quality"]'
        );
        if (direct && visible(direct)) { direct.click(); return true; }

        const item = Array.from(document.querySelectorAll(
          '[role="menuitem"], button, [data-a-target*="settings-menu-item"]'
        ))
        .filter(visible)
        .find(el => {
          const text = (el.innerText || el.textContent || "").trim();
          return /quality/i.test(text) || /جودة/i.test(text);
        });

        if (item) { item.click(); return true; }
        return false;
      })();`
    });

    return !!result?.[0];
  } catch {
    return false;
  }
}

async function clickLowestQualityOption(tabId) {
  try {
    const result = await browser.tabs.executeScript(tabId, {
      code: `(() => {
        const visible = el => {
          if (!el) return false;
          const s = getComputedStyle(el);
          const r = el.getBoundingClientRect();
          return s.display !== "none" && s.visibility !== "hidden" &&
                 r.width > 0 && r.height > 0;
        };

        const qualityNumber = text => {
          const m = String(text || "").match(/(\\d{3,4})p(?:\\d+)?/i);
          return m ? Number(m[1]) : Number.POSITIVE_INFINITY;
        };

        const candidates = Array.from(document.querySelectorAll(
          '[role="menuitemradio"], [role="radio"], button, [data-a-target*="quality"]'
        ))
        .filter(visible)
        .map(el => ({
          el,
          text: (el.innerText || el.textContent || "").trim()
        }))
        .filter(x => /(\\d{3,4})p(?:\\d+)?/i.test(x.text))
        .sort((a,b) => qualityNumber(a.text) - qualityNumber(b.text));

        if (!candidates.length)
          return { ok:false, label:null, changed:false };

        const lowest = candidates[0];
        const checked =
          lowest.el.getAttribute("aria-checked") === "true" ||
          lowest.el.getAttribute("data-selected") === "true";

        if (!checked) lowest.el.click();

        return { ok:true, label:lowest.text, changed:!checked };
      })();`
    });

    return result?.[0] || { ok:false, label:null, changed:false };
  } catch {
    return { ok:false, label:null, changed:false };
  }
}

async function ensureLowestQuality(channel, tabId, force = false) {
  if (!lastState?.ForceLowestQuality) return;

  const now = Date.now();
  const state = QUALITY_STATE.get(tabId) || {
    appliedForDocument: false,
    lastAttemptAt: 0,
    lastLabel: null,
    failures: 0,
    graceUntil: 0
  };

  // Important UX rule:
  // Apply the lowest quality ONCE after page load/reload.
  // If the user later manually chooses a higher quality to watch, leave it alone.
  if (state.appliedForDocument && !force) return;

  if (!force && now - state.lastAttemptAt < QUALITY_RETRY_MS) return;
  state.lastAttemptAt = now;

  let options = await inspectQualityOptions(tabId);

  if (!options.length) {
    const openedSettings = await clickPlayerSettings(tabId);
    if (openedSettings)
      await new Promise(resolve => setTimeout(resolve, 300));

    options = await inspectQualityOptions(tabId);

    if (!options.length) {
      const openedQuality = await clickQualityMenuItem(tabId);
      if (openedQuality)
        await new Promise(resolve => setTimeout(resolve, 300));
    }
  }

  const applied = await clickLowestQualityOption(tabId);

  if (applied.ok) {
    state.failures = 0;
    state.appliedForDocument = true;
    state.graceUntil = Date.now() + QUALITY_CHANGE_GRACE_MS;

    const raw = String(applied.label || "").replace(/\s+/g, " ").trim();
    const match = raw.match(/(\d{3,4}p(?:\d+)?)/i);
    const normalized = match ? match[1].toLowerCase() : raw;

    state.lastLabel = normalized || state.lastLabel;

    if (applied.changed) {
      setBrowserEvent(
        channel,
        "QUALITY-SET",
        `lowest available quality selected: ${normalized || raw}`
      );
    } else {
      log("QUALITY already lowest:", channel, normalized || raw);
    }
  } else {
    // Twitch DOM may not be ready yet. Retry later, but never reload the
    // stream merely because the quality menu could not be found.
    state.failures += 1;
  }

  QUALITY_STATE.set(tabId, state);
}

async function getPlaybackStatus(tabId, tryStart = false) {
  const probeCode = `(() => {
    const videos = Array.from(document.querySelectorAll("video"));

    const score = video => {
      const r = video.getBoundingClientRect();
      const s = getComputedStyle(video);
      const visible =
        s.display !== "none" &&
        s.visibility !== "hidden" &&
        r.width > 1 &&
        r.height > 1;

      // Prefer the actual large visible Twitch player over hidden/preload video
      // elements that can sit at currentTime=0.
      return (visible ? 1_000_000_000 : 0) + Math.max(0, r.width * r.height);
    };

    const video = videos.sort((a,b) => score(b) - score(a))[0] || null;

    if (!video) {
      return {
        hasVideo: false,
        paused: true,
        ended: false,
        readyState: 0,
        networkState: 0,
        currentTime: 0,
        totalVideoFrames: 0,
        videoWidth: 0,
        videoHeight: 0
      };
    }

    let totalVideoFrames = 0;
    try {
      const q = video.getVideoPlaybackQuality?.();
      totalVideoFrames = Number(q?.totalVideoFrames || 0);
    } catch {}

    return {
      hasVideo: true,
      paused: video.paused,
      ended: video.ended,
      readyState: video.readyState,
      networkState: video.networkState,
      currentTime: Number(video.currentTime || 0),
      totalVideoFrames,
      videoWidth: Number(video.videoWidth || 0),
      videoHeight: Number(video.videoHeight || 0)
    };
  })();`;

  try {
    const result = await browser.tabs.executeScript(tabId, { code: probeCode });

    let status = result?.[0] || {
      hasVideo: false,
      paused: true,
      ended: false,
      readyState: 0,
      networkState: 0,
      currentTime: 0,
      totalVideoFrames: 0,
      videoWidth: 0,
      videoHeight: 0
    };

    if (tryStart && status.hasVideo && status.paused) {
      await browser.tabs.executeScript(tabId, {
        code: `(() => {
          const videos = Array.from(document.querySelectorAll("video"));
          const score = video => {
            const r = video.getBoundingClientRect();
            const s = getComputedStyle(video);
            const visible =
              s.display !== "none" &&
              s.visibility !== "hidden" &&
              r.width > 1 &&
              r.height > 1;
            return (visible ? 1_000_000_000 : 0) + Math.max(0, r.width * r.height);
          };
          const video = videos.sort((a,b) => score(b) - score(a))[0] || null;
          if (!video) return false;

          // Never touch video.muted or video.volume.
          // StreakWatch mute stays Firefox tab-level only.
          try {
            const p = video.play();
            if (p && typeof p.catch === "function") p.catch(() => {});
          } catch {}
          return true;
        })();`
      });

      await new Promise(resolve => setTimeout(resolve, 1200));
      const after = await browser.tabs.executeScript(tabId, { code: probeCode });
      status = after?.[0] || status;
    }

    return status;
  } catch {
    return {
      hasVideo: false,
      paused: true,
      ended: false,
      readyState: 0,
      networkState: 0,
      currentTime: 0,
      totalVideoFrames: 0,
      videoWidth: 0,
      videoHeight: 0
    };
  }
}

async function ensurePlayback(channel, tabId) {
  const now0 = Date.now();
  let state = PLAYBACK_STATE.get(tabId) || {
    lastTime: null,
    lastFrames: null,
    lastProgressAt: now0,
    firstSeenAt: now0,
    startupGraceUntil: now0 + PLAYBACK_STARTUP_GRACE_MS,
    noVideoSince: null,
    problemSince: null,
    consecutiveBadSamples: 0,
    startAttempts: 0,
    lastStartAttemptAt: 0,
    reloadAttempts: 0,
    lastReloadAt: 0
  };

  // Never diagnose/reload a Twitch player while the desktop knows networking
  // is down or while a reconnect is still settling.
  if (inNetworkGrace()) {
    state.problemSince = null;
    state.noVideoSince = null;
    state.consecutiveBadSamples = 0;
    state.lastProgressAt = Date.now();
    setReport(channel, bridgeNetworkHealthy() ? "Network recovery settling" : "Waiting for network", tabId, 0);
    PLAYBACK_STATE.set(tabId, state);
    return;
  }

  let status = await getPlaybackStatus(tabId, false);
  let now = Date.now();

  if (!status.hasVideo) {
    state.noVideoSince ??= now;
    state.consecutiveBadSamples += 1;

    setReport(
      channel, "Waiting for player", tabId, state.reloadAttempts, undefined,
      telemetryFrom(status, false, state)
    );

    const noVideoFor = now - state.noVideoSince;
    const outsideStartupGrace = now >= Number(state.startupGraceUntil || 0);

    if (
      outsideStartupGrace &&
      noVideoFor >= PLAYBACK_NO_VIDEO_RELOAD_MS &&
      state.consecutiveBadSamples >= 6 &&
      now - state.lastReloadAt >= PLAYBACK_RELOAD_COOLDOWN_MS &&
      state.reloadAttempts < PLAYBACK_MAX_RELOADS
    ) {
      state.reloadAttempts += 1;
      state.lastReloadAt = now;
      setBrowserEvent(
        channel,
        "STUCK-PLAYER",
        `player missing for ${Math.round(noVideoFor/1000)}s across ${state.consecutiveBadSamples} checks; one recovery reload`
      );
      setReport(channel, "Recovering: player missing", tabId, state.reloadAttempts);

      try { await browser.tabs.reload(tabId, { bypassCache: false }); } catch {}

      state.lastTime = null;
      state.lastFrames = null;
      state.lastProgressAt = Date.now();
      state.firstSeenAt = Date.now();
      state.startupGraceUntil = Date.now() + PLAYBACK_STARTUP_GRACE_MS;
      state.noVideoSince = null;
      state.problemSince = null;
      state.consecutiveBadSamples = 0;
    }

    PLAYBACK_STATE.set(tabId, state);
    return;
  }

  state.noVideoSince = null;

  if (state.lastTime === null) {
    state.lastTime = Number(status.currentTime || 0);
    state.lastFrames = Number(status.totalVideoFrames || 0);
    state.lastProgressAt = now;
    state.consecutiveBadSamples = 0;
    setReport(
      channel, "Verifying playback", tabId, state.reloadAttempts, undefined,
      telemetryFrom(status, false, state)
    );
    PLAYBACK_STATE.set(tabId, state);
    return;
  }

  const delta = Number(status.currentTime || 0) - Number(state.lastTime || 0);
  const frameDelta = Number(status.totalVideoFrames || 0) - Number(state.lastFrames || 0);
  const progressing = delta > 0.35 || frameDelta >= 3;

  if (progressing) {
    state.lastTime = Number(status.currentTime || 0);
    state.lastFrames = Number(status.totalVideoFrames || 0);
    state.lastProgressAt = now;
    state.problemSince = null;
    state.consecutiveBadSamples = 0;
    state.startAttempts = 0;
    state.reloadAttempts = 0;

    setReport(
      channel, "Playing", tabId, 0, new Date().toISOString(),
      telemetryFrom(status, true, state, null)
    );

    PLAYBACK_STATE.set(tabId, state);
    await recordLowDataProgress(channel, tabId);
    return;
  }

  // A seek/rendition reset is not a failure; establish a new baseline.
  if (delta < -1 || frameDelta < -3) {
    state.lastTime = Number(status.currentTime || 0);
    state.lastFrames = Number(status.totalVideoFrames || 0);
    state.lastProgressAt = now;
    state.consecutiveBadSamples = 0;
    setReport(channel, "Verifying playback", tabId, 0);
    PLAYBACK_STATE.set(tabId, state);
    return;
  }

  state.consecutiveBadSamples += 1;
  state.problemSince ??= now;

  // First recovery step is always a gentle play() request. It does not alter
  // Twitch player mute/volume.
  if (now - state.lastStartAttemptAt >= PLAYBACK_START_RETRY_MS) {
    state.startAttempts += 1;
    state.lastStartAttemptAt = now;
    status = await getPlaybackStatus(tabId, true);
    now = Date.now();

    const afterDelta = Number(status.currentTime || 0) - Number(state.lastTime || 0);
    const afterFrameDelta = Number(status.totalVideoFrames || 0) - Number(state.lastFrames || 0);

    if (afterDelta > 0.35 || afterFrameDelta >= 3) {
      state.lastTime = Number(status.currentTime || 0);
      state.lastFrames = Number(status.totalVideoFrames || 0);
      state.lastProgressAt = now;
      state.problemSince = null;
      state.consecutiveBadSamples = 0;
      state.startAttempts = 0;
      state.reloadAttempts = 0;

      setReport(
        channel, "Playing", tabId, 0, new Date().toISOString(),
        telemetryFrom(status, true, state, null)
      );
      PLAYBACK_STATE.set(tabId, state);
      await recordLowDataProgress(channel, tabId);
      return;
    }
  }

  const stalledFor = now - state.lastProgressAt;
  const outsideStartupGrace = now >= Number(state.startupGraceUntil || 0);

  // Reload is a last resort only: >=75 seconds, >=8 bad samples, network
  // healthy, outside startup/reconnect grace, and max ONE reload before a long
  // cooldown. This eliminates the previous reload/QUALITY loop.
  if (
    outsideStartupGrace &&
    stalledFor >= PLAYBACK_STALL_MS &&
    state.consecutiveBadSamples >= 8 &&
    now - state.lastReloadAt >= PLAYBACK_RELOAD_COOLDOWN_MS &&
    state.reloadAttempts < PLAYBACK_MAX_RELOADS
  ) {
    state.reloadAttempts += 1;
    state.lastReloadAt = now;

    setBrowserEvent(
      channel,
      "STUCK-PLAYER",
      `confirmed stall ${Math.round(stalledFor/1000)}s across ${state.consecutiveBadSamples} checks | time=${Number(status.currentTime||0).toFixed(1)}s | frames=${Number(status.totalVideoFrames||0)} | one recovery reload`
    );

    setReport(channel, "Recovering: confirmed stuck player", tabId, state.reloadAttempts);

    try { await browser.tabs.reload(tabId, { bypassCache: false }); } catch {}

    state.lastTime = null;
    state.lastFrames = null;
    state.lastProgressAt = Date.now();
    state.firstSeenAt = Date.now();
    state.startupGraceUntil = Date.now() + PLAYBACK_STARTUP_GRACE_MS;
    state.problemSince = null;
    state.noVideoSince = null;
    state.consecutiveBadSamples = 0;
    state.startAttempts = 0;
  } else {
    setReport(
      channel,
      status.paused ? "Starting playback" : "Verifying playback",
      tabId,
      state.startAttempts,
      undefined,
      telemetryFrom(status, false, state)
    );
  }

  PLAYBACK_STATE.set(tabId, state);
}

async function scanManagedPlayback() {
  if (!lastState?.AppRunning) return;

  for (const [channel, tabId] of MANAGED.entries()) {
    try {
      const tab = await browser.tabs.get(tabId);
      if (channelFromUrl(tab.url || "") !== channel) continue;

      await ensurePlayback(channel, tabId);
    } catch {
      PLAYBACK_STATE.delete(tabId);
    }
  }
}

async function detectPlayerError2000(tabId) {
  try {
    const result = await browser.tabs.executeScript(tabId, {
      code: `(() => {
        const bodyText = (document.body?.innerText || "").toLowerCase();
        const has2000 = bodyText.includes("error #2000") || bodyText.includes("error # 2000") || bodyText.includes("(error #2000)");
        const reloadButton = [...document.querySelectorAll("button")].find(b => /reload player/i.test((b.innerText || "").trim()));
        return { has2000, hasReloadButton: !!reloadButton };
      })();`
    });
    return result?.[0] || { has2000: false, hasReloadButton: false };
  } catch {
    return { has2000: false, hasReloadButton: false };
  }
}

async function recoverPlayerIfNeeded(channel, tabId) {
  if (inNetworkGrace()) return;
  const now = Date.now();
  const state = PLAYER_RECOVERY.get(tabId) || { attempts: 0, lastAttemptAt: 0, lastHealthyAt: now };
  const detected = await detectPlayerError2000(tabId);

  if (!detected.has2000 && !detected.hasReloadButton) {
    state.lastHealthyAt = now;
    if (state.attempts > 0 && now - state.lastAttemptAt > PLAYER_RECOVERY_COOLDOWN_MS) state.attempts = 0;
    PLAYER_RECOVERY.set(tabId, state);
    return;
  }

  if (now - state.lastAttemptAt < PLAYER_RECOVERY_RETRY_MS) return;
  if (state.attempts >= PLAYER_RECOVERY_MAX_ATTEMPTS) {
    if (now - state.lastAttemptAt < PLAYER_RECOVERY_COOLDOWN_MS) return;
    state.attempts = 0;
  }

  state.attempts += 1;
  state.lastAttemptAt = now;
  PLAYER_RECOVERY.set(tabId, state);
  setReport(channel, "Error #2000 - recovering", tabId, state.attempts);
  log(`Player Error #2000 detected: ${channel} tab=${tabId} attempt=${state.attempts}/${PLAYER_RECOVERY_MAX_ATTEMPTS}`);

  try {
    await browser.tabs.reload(tabId, { bypassCache: false });
    PLAYBACK_STATE.delete(tabId);
    QUALITY_STATE.delete(tabId);
    queueActivation(channel, tabId);
  } catch (e) {
    log("Player recovery reload failed:", channel, tabId, e);
  }
}

async function scanManagedPlayers() {
  if (!lastState?.AppRunning) return;
  for (const [channel, tabId] of MANAGED.entries()) {
    try {
      const tab = await browser.tabs.get(tabId);
      if (channelFromUrl(tab.url || "") !== channel) continue;
      await recoverPlayerIfNeeded(channel, tabId);
    } catch {
      PLAYER_RECOVERY.delete(tabId);
    }
  }
}

function requestState() {
  if (!port) return;
  try {
    port.postMessage({
      Type: "get-state",
      ExtensionVersion: browser.runtime.getManifest().version,
      At: Date.now(),
      Reports: Object.fromEntries(REPORTS.entries())
    });
  } catch {}
}

function connectNative() {
  try {
    port = browser.runtime.connectNative(HOST);

    port.onMessage.addListener(async message => {
      lastState = message;
      handleSessionState(message);

      if (message?.NetworkHealthy === false) {
        networkGraceUntil = Date.now() + NETWORK_RECOVERY_GRACE_MS;
      }

      await reconcile();
      await handleNetworkRecoverySignal(message);
    });

    port.onDisconnect.addListener(() => {
      const err = browser.runtime.lastError;
      if (err) log("Native host disconnected:", err.message);
      port = null;
      lastState = null;
      scheduleReconnect();
    });

    requestState();
  } catch (e) {
    log("Native host connection failed:", e);
    port = null;
    scheduleReconnect();
  }
}

function scheduleReconnect() {
  if (reconnectTimer) return;

  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connectNative();
  }, 5000);
}

browser.tabs.onRemoved.addListener(tabId => {
  PLAYER_RECOVERY.delete(tabId); PLAYBACK_STATE.delete(tabId); QUALITY_STATE.delete(tabId);
  for(let i=ACTIVATION_QUEUE.length-1;i>=0;i--)if(ACTIVATION_QUEUE[i].tabId===tabId)ACTIVATION_QUEUE.splice(i,1);
  for(const [channel,managedTabId] of MANAGED.entries()){
    if(managedTabId!==tabId)continue;
    MANAGED.delete(channel);
    const intentional=INTENTIONAL_CLOSE.has(tabId); INTENTIONAL_CLOSE.delete(tabId);
    if(intentional){
      if(!isLowDataDone(channel)) setReport(channel,"Offline",null,0);
      break;
    }
    setReport(channel,"Tab closed - recovering",null,1);
    setBrowserEvent(channel,"TAB-MISSING",`managed LIVE tab ${tabId} was closed; recovery queued`);
    scheduleSingleReopen(channel,1800);
    break;
  }
});

browser.tabs.onUpdated.addListener(async (tabId, changeInfo) => {
  let managedChannel=null;
  for(const [channel,managedTabId] of MANAGED.entries())if(managedTabId===tabId){managedChannel=channel;break;}
  if(!managedChannel)return;

  // A real page reload/navigation creates a new quality-application session.
  // Manual Twitch quality changes do not trigger this, so the user's higher
  // quality choice remains untouched until the next reload.
  if(changeInfo.status === "loading"){
    QUALITY_STATE.delete(tabId);
    PLAYBACK_STATE.delete(tabId);
  }

  if(changeInfo.status === "complete" && bridgeNetworkHealthy() && !isLowDataDone(managedChannel)){
    queueActivation(managedChannel, tabId);
  }

  if(lastState?.KeepManagedTabsMuted && changeInfo.mutedInfo)await muteManagedTab(tabId,managedChannel);
  if(changeInfo.url){
    const currentChannel=channelFromUrl(changeInfo.url);
    if(currentChannel!==managedChannel){
      // If the desktop still says the original channel is LIVE, navigation or
      // a raid must not permanently remove the protected managed tab.
      cancelReopen(managedChannel);
      MANAGED.delete(managedChannel);
      setReport(managedChannel,"Tab missing - recovering",null,1);
      setBrowserEvent(
        managedChannel,
        "TAB-MISSING",
        `managed tab navigated away to ${currentChannel || "non-channel page"}; recovery queued`
      );
      scheduleSingleReopen(managedChannel,1800);
    }
  }
});

// Browser-level fallback: if Firefox itself notices connectivity returning,
 // recover managed LIVE pages even if the desktop probe is still catching up.
window.addEventListener("online", () => {
  networkGraceUntil = Date.now() + NETWORK_RECOVERY_GRACE_MS;
  setTimeout(
    () => reloadManagedAfterNetworkRestore("firefox-online"),
    1200
  );
});

// One state request every 2 seconds is enough.
// Reconciliation is also serialized, so overlapping callbacks cannot duplicate tabs.
setInterval(requestState, 2000);
setInterval(() => reconcile(), 2500);
setInterval(() => scanManagedPlayers(), 10000);

// Verify that managed Twitch videos are actually advancing.
// If paused, request play(); if the video clock stalls, recover automatically.
setInterval(() => scanManagedPlayback(), 10000);

connectNative();
