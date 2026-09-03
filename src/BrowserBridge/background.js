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
const PLAYBACK_STALL_MS = 12000;
const PLAYBACK_START_RETRY_MS = 3000;
let reconcileRequested = false;
let activationQueueRunning = false;
const ACTIVATION_QUEUE = [];
let lastNetworkRecoveryGeneration = null;
let lastNetworkRecoveryReloadAt = 0;
const INTENTIONAL_CLOSE = new Set();
const SUPPRESS_STREAM = new Map();
const REOPEN_TIMERS = new Map();
const NETWORK_VERIFY = new Map();
const NETWORK_VERIFY_MAX = 2;
const DEDUPE_RUNNING = new Set();

function log(...args) {
  console.log("[StreakWatch Bridge]", ...args);
}

function setReport(channel, status, tabId = null, failureCount = 0, lastSuccessfulPlaybackUtc = undefined) {
  const previous = REPORTS.get(channel) || {};
  REPORTS.set(channel, {
    Status: status,
    TabId: tabId,
    FailureCount: failureCount,
    LastSuccessfulPlaybackUtc:
      lastSuccessfulPlaybackUtc !== undefined
        ? lastSuccessfulPlaybackUtc
        : (previous.LastSuccessfulPlaybackUtc || null)
  });
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
    if (!lastState?.AppRunning || !live.includes(channel) || isSuppressedForCurrentStream(channel)) return;
    await reconcile();
  }, delayMs);
  REOPEN_TIMERS.set(channel, timer);
}
function cancelReopen(channel) {
  const t = REOPEN_TIMERS.get(channel); if (t) clearTimeout(t);
  REOPEN_TIMERS.delete(channel);
}

function queueActivation(channel, tabId) {
  if (!ACTIVATION_QUEUE.some(x => x.tabId === tabId))
    ACTIVATION_QUEUE.push({ channel, tabId });

  runActivationQueue();
}

async function runActivationQueue() {
  if (activationQueueRunning) return;
  activationQueueRunning = true;

  try {
    while (ACTIVATION_QUEUE.length > 0) {
      const item = ACTIVATION_QUEUE.shift();

      try {
        const tab = await browser.tabs.get(item.tabId);
        await browser.tabs.update(item.tabId, { active: true });
        await browser.windows.update(tab.windowId, { focused: true });

        // Give Twitch a short foreground initialization window, then continue
        // to the next newly-opened LIVE channel. This happens once per new tab.
        await new Promise(resolve => setTimeout(resolve, 2200));
        await ensurePlayback(item.channel, item.tabId);
      } catch (e) {
        log("Activation queue item failed:", item.channel, item.tabId, e);
      }
    }
  } finally {
    activationQueueRunning = false;
  }
}

async function verifyAfterNetworkRecovery(channel, tabId, attempt = 0) {
  try {
    const tab = await browser.tabs.get(tabId);
    if (channelFromUrl(tab.url || "") !== channel) return;
    const status = await getPlaybackStatus(tabId, true);
    if (status.hasVideo && !status.paused && status.readyState >= 2) {
      setReport(channel, "Playing", tabId, 0, new Date().toISOString());
      NETWORK_VERIFY.delete(channel); return;
    }
    if (attempt >= NETWORK_VERIFY_MAX) {
      setReport(channel, "Playback needs attention", tabId, attempt);
      NETWORK_VERIFY.delete(channel); return;
    }
    await browser.tabs.reload(tabId, { bypassCache: false });
    const timer=setTimeout(()=>verifyAfterNetworkRecovery(channel,tabId,attempt+1),10000);
    NETWORK_VERIFY.set(channel,{attempts:attempt+1,timer});
  } catch { NETWORK_VERIFY.delete(channel); }
}
async function reloadManagedAfterNetworkRestore(reason = "network-restored") {
  const now=Date.now(); if(now-lastNetworkRecoveryReloadAt<10000)return;
  lastNetworkRecoveryReloadAt=now;
  const wanted=new Set((lastState?.LiveChannels||[]).map(c=>String(c).toLowerCase()));
  log(`Network recovery: ${reason}; recovering managed LIVE tabs`);
  for(const channel of wanted){
    if(isSuppressedForCurrentStream(channel))continue;
    let tabId=MANAGED.get(channel);
    if(tabId==null){const ex=await findTwitchTabsByChannel(channel);if(ex.length){tabId=ex[0].id;MANAGED.set(channel,tabId);}}
    if(tabId==null)continue;
    try{
      const old=NETWORK_VERIFY.get(channel);if(old?.timer)clearTimeout(old.timer);
      setReport(channel,"Recovering: network restored",tabId,0);
      await browser.tabs.reload(tabId,{bypassCache:false});
      const timer=setTimeout(()=>verifyAfterNetworkRecovery(channel,tabId,0),10000);
      NETWORK_VERIFY.set(channel,{attempts:0,timer});
      await new Promise(r=>setTimeout(r,700));
    }catch(e){log("Network recovery reload failed:",channel,tabId,e);}
  }
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

async function findTwitchTabsByChannel(channel) {
  const tabs = await twitchTabs();
  const normalized = channel.toLowerCase();
  return tabs.filter(t => channelFromUrl(t.url || "") === normalized);
}

async function dedupeChannelTabs(channel, preferredTabId = null) {
  const normalized = channel.toLowerCase();
  if (DEDUPE_RUNNING.has(normalized)) return preferredTabId;

  DEDUPE_RUNNING.add(normalized);
  try {
    const tabs = await findTwitchTabsByChannel(normalized);
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
        // These are duplicate tabs for a channel StreakWatch currently manages.
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
  if (isSuppressedForCurrentStream(normalized)) {
    setReport(normalized, "Raid/redirect - not reopening", null, 0);
    return null;
  }
  if (!REPORTS.has(normalized))
    setReport(normalized, "Opening");

  if (await managedTabStillExists(normalized))
    return MANAGED.get(normalized);

  if (OPENING.has(normalized))
    return null;

  const existing = await findTwitchTabsByChannel(normalized);
  if (existing.length > 0) {
    const keepId = await dedupeChannelTabs(normalized, existing[0].id);
    const tab = await browser.tabs.get(keepId);
    MANAGED.set(normalized, tab.id);
    setReport(normalized, "Starting", tab.id);
    await muteManagedTab(tab.id, normalized);
    queueActivation(normalized, tab.id);
    return tab.id;
  }

  OPENING.add(normalized);
  try {
    const secondCheck = await findTwitchTabsByChannel(normalized);
    if (secondCheck.length > 0) {
      const keepId = await dedupeChannelTabs(normalized, secondCheck[0].id);
      const tab = await browser.tabs.get(keepId);
      MANAGED.set(normalized, tab.id);
      setReport(normalized, "Starting", tab.id);
      await muteManagedTab(tab.id, normalized);
      queueActivation(normalized, tab.id);
      return tab.id;
    }

    // Create ALL missing LIVE channels immediately. Do not block creation of
    // channel #2/#3 while waiting for channel #1 to initialize.
    const tab = await browser.tabs.create({
      url: `https://www.twitch.tv/${encodeURIComponent(normalized)}`,
      active: false
    });

    MANAGED.set(normalized, tab.id);
    setReport(normalized, "Starting", tab.id);
    await muteManagedTab(tab.id, normalized);

    log("Opened managed tab:", normalized, tab.id);

    // A Firefox-startup/browser-watchdog race or another bridge instance can
    // still create a second copy within a few hundred ms. Sweep once more.
    setTimeout(() => dedupeChannelTabs(normalized, tab.id), 1200);

    // Foreground initialization is queued separately, once per new tab.
    queueActivation(normalized, tab.id);
    return tab.id;
  } catch (e) {
    log("Failed to create tab", normalized, e);
    setReport(normalized, "Open failed", null, 1);
    return null;
  } finally {
    OPENING.delete(normalized);
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
  const orderedWanted = [...recoveryLive, ...normalLive];
  const wanted = new Set(orderedWanted);
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

  // Create/adopt all wanted LIVE channel tabs in one batch.
  // OPENING + MANAGED still protect against duplicate tabs per channel.
  for (const channel of recoveryLive)
    await ensureChannel(channel);

  await Promise.all(normalLive.map(channel => ensureChannel(channel)));

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


async function getPlaybackStatus(tabId, tryStart = false) {
  try {
    const result = await browser.tabs.executeScript(tabId, {
      code: `(() => {
        const video = document.querySelector("video");
        if (!video) {
          return {
            hasVideo: false,
            paused: true,
            ended: false,
            readyState: 0,
            currentTime: 0
          };
        }

        return {
          hasVideo: true,
          paused: video.paused,
          ended: video.ended,
          readyState: video.readyState,
          currentTime: Number(video.currentTime || 0)
        };
      })();`
    });

    let status = result?.[0] || {
      hasVideo: false,
      paused: true,
      ended: false,
      readyState: 0,
      currentTime: 0
    };

    if (tryStart && status.hasVideo && status.paused) {
      await browser.tabs.executeScript(tabId, {
        code: `(() => {
          const video = document.querySelector("video");
          if (!video) return false;

          // Do NOT change video.muted or video.volume.
          // Per-channel mute is handled only at Firefox tab level.
          try {
            const p = video.play();
            if (p && typeof p.catch === "function") p.catch(() => {});
          } catch {}

          return true;
        })();`
      });

      await new Promise(resolve => setTimeout(resolve, 1200));

      const after = await browser.tabs.executeScript(tabId, {
        code: `(() => {
          const video = document.querySelector("video");
          return video ? {
            hasVideo: true,
            paused: video.paused,
            ended: video.ended,
            readyState: video.readyState,
            currentTime: Number(video.currentTime || 0)
          } : {
            hasVideo: false,
            paused: true,
            ended: false,
            readyState: 0,
            currentTime: 0
          };
        })();`
      });

      status = after?.[0] || status;
    }

    return status;
  } catch {
    return {
      hasVideo: false,
      paused: true,
      ended: false,
      readyState: 0,
      currentTime: 0
    };
  }
}


async function ensurePlayback(channel, tabId) {
  let state = PLAYBACK_STATE.get(tabId) || {
    lastTime: -1,
    lastProgressAt: Date.now(),
    firstSeenAt: Date.now(),
    noVideoSince: null,
    startAttempts: 0,
    lastStartAttemptAt: 0
  };

  let status = await getPlaybackStatus(tabId, false);
  const now = Date.now();

  if (!status.hasVideo) {
    state.noVideoSince ??= now;
    setReport(channel, "Waiting for player", tabId, state.startAttempts);

    // Twitch pages can take a while to initialize. Do not reload/focus-spam.
    // Error #2000 has its own explicit recovery path.
    PLAYBACK_STATE.set(tabId, state);
    return;
  }

  state.noVideoSince = null;

  const progressing = status.currentTime > state.lastTime + 0.25;

  if (!status.paused && status.readyState >= 2) {
    if (progressing) {
      state.lastTime = status.currentTime;
      state.lastProgressAt = now;
      state.startAttempts = 0;
      setReport(channel, "Playing", tabId, 0, new Date().toISOString());
    } else {
      // Don't reload just because currentTime didn't advance during one scan.
      // Background tabs / Twitch buffering can legitimately delay it.
      setReport(channel, "Playing", tabId, state.startAttempts);
    }

    PLAYBACK_STATE.set(tabId, state);
    return;
  }

  if (status.paused && now - state.lastStartAttemptAt >= 15000) {
    state.startAttempts += 1;
    state.lastStartAttemptAt = now;
    setReport(channel, "Starting playback", tabId, state.startAttempts);

    log(
      `Playback start requested: ${channel} tab=${tabId} ` +
      `attempt=${state.startAttempts}`
    );

    // One gentle play() request. No tab activation, no window focus, no reload loop.
    status = await getPlaybackStatus(tabId, true);

    if (!status.paused && status.readyState >= 2) {
      state.lastProgressAt = Date.now();
      state.lastTime = status.currentTime;
      state.startAttempts = 0;
      setReport(channel, "Playing", tabId, 0, new Date().toISOString());
      log(`Playback started: ${channel} tab=${tabId}`);
    }
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
    await browser.tabs.update(tabId, { active: true });
    const tab = await browser.tabs.get(tabId);
    await browser.windows.update(tab.windowId, { focused: true });
    await new Promise(resolve => setTimeout(resolve, 2500));
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
  PLAYER_RECOVERY.delete(tabId); PLAYBACK_STATE.delete(tabId);
  for(let i=ACTIVATION_QUEUE.length-1;i>=0;i--)if(ACTIVATION_QUEUE[i].tabId===tabId)ACTIVATION_QUEUE.splice(i,1);
  for(const [channel,managedTabId] of MANAGED.entries()){
    if(managedTabId!==tabId)continue;
    MANAGED.delete(channel);
    const intentional=INTENTIONAL_CLOSE.has(tabId); INTENTIONAL_CLOSE.delete(tabId);
    if(intentional){setReport(channel,"Offline",null,0);break;}
    setReport(channel,"Tab closed - recovering",null,1);
    scheduleSingleReopen(channel,1800);
    break;
  }
});

browser.tabs.onUpdated.addListener(async (tabId, changeInfo) => {
  let managedChannel=null;
  for(const [channel,managedTabId] of MANAGED.entries())if(managedTabId===tabId){managedChannel=channel;break;}
  if(!managedChannel)return;
  if(lastState?.KeepManagedTabsMuted && changeInfo.mutedInfo)await muteManagedTab(tabId,managedChannel);
  if(changeInfo.url){
    const currentChannel=channelFromUrl(changeInfo.url);
    if(currentChannel!==managedChannel){
      const streamId=currentStreamId(managedChannel);
      if(streamId)SUPPRESS_STREAM.set(managedChannel,streamId);
      cancelReopen(managedChannel);
      MANAGED.delete(managedChannel);
      setReport(managedChannel,"Raid/redirect",null,0);
      log("Raid/redirect: suppress old stream reopen",managedChannel,streamId,"->",currentChannel);
    }
  }
});

// Browser-level fallback: if Firefox itself notices connectivity returning,
 // recover managed LIVE pages even if the desktop probe is still catching up.
window.addEventListener("online", () => {
  setTimeout(
    () => reloadManagedAfterNetworkRestore("firefox-online"),
    1200
  );
});

// One state request every 2 seconds is enough.
// Reconciliation is also serialized, so overlapping callbacks cannot duplicate tabs.
setInterval(requestState, 2000);
setInterval(() => reconcile(), 2500);
setInterval(() => scanManagedPlayers(), 5000);

// Verify that managed Twitch videos are actually advancing.
// If paused, request play(); if the video clock stalls, recover automatically.
setInterval(() => scanManagedPlayback(), 5000);

connectNative();
