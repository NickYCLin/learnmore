#!/usr/bin/env node

const { chromium } = require("playwright");

const DEFAULT_URL = "https://magicplus-design.serveirc.com/LearnMore/Lyrics/ea3d8e96-fb5c-4bff-bf47-a9683e844eff";
const DEFAULT_VIEWPORT_WIDTH = 1365;
const DEFAULT_VIEWPORT_HEIGHT = 900;
const DEFAULT_VOCAL_DECODE_TIMEOUT_MS = 120000;
const EXPECTED_SAMPLE_AVATARS = [
  { id: "mao_pro", runtime: "live2d", label: "mao_pro" },
  { id: "hiyori_pro", runtime: "live2d", label: "Hiyori Momose" }
];
const BLOCKED_LEARNMORE_AVATAR_IDS = [
  "mika_live2d",
  "mika_stretchy_test",
  "mika_formal_2d",
  "mika_vrm",
  "mika_formal_vrm",
  "miara_pro",
  "kei_vowels_pro",
  "ren_foster"
];

function parseArgs(argv) {
  const args = {
    url: DEFAULT_URL,
    chromePath: process.env.CHROME_PATH || "/usr/bin/google-chrome",
    viewportWidth: DEFAULT_VIEWPORT_WIDTH,
    viewportHeight: DEFAULT_VIEWPORT_HEIGHT,
    vocalDecodeTimeoutMs: parseTimeout(
      process.env.MIKA_VOCAL_DECODE_TIMEOUT_MS || String(DEFAULT_VOCAL_DECODE_TIMEOUT_MS),
      "vocal decode timeout"
    ),
    headful: false
  };

  for (let i = 2; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--url" && argv[i + 1]) {
      args.url = argv[++i];
    } else if (arg === "--chrome-path" && argv[i + 1]) {
      args.chromePath = argv[++i];
    } else if (arg === "--viewport-width" && argv[i + 1]) {
      args.viewportWidth = parseViewportSize(argv[++i], "viewport width");
    } else if (arg === "--viewport-height" && argv[i + 1]) {
      args.viewportHeight = parseViewportSize(argv[++i], "viewport height");
    } else if (arg === "--vocal-decode-timeout-ms" && argv[i + 1]) {
      args.vocalDecodeTimeoutMs = parseTimeout(argv[++i], "vocal decode timeout");
    } else if (arg === "--headful") {
      args.headful = true;
    } else if (arg === "--help" || arg === "-h") {
      printHelp();
      process.exit(0);
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }

  return args;
}

function parseTimeout(value, label) {
  const timeout = Number(value);
  if (!Number.isInteger(timeout) || timeout < 1000 || timeout > 300000) {
    throw new Error(`Invalid ${label}: ${value}`);
  }
  return timeout;
}

function parseViewportSize(value, label) {
  const size = Number(value);
  if (!Number.isInteger(size) || size < 320 || size > 3840) {
    throw new Error(`Invalid ${label}: ${value}`);
  }
  return size;
}

function printHelp() {
  console.log(`Usage: node scripts/learnmore_mika_avatar_switch_smoke.cjs [options]

Options:
  --url <url>             Lyrics page to test.
  --chrome-path <path>    Chrome/Chromium executable. Default: CHROME_PATH or /usr/bin/google-chrome.
  --viewport-width <px>   Browser viewport width. Default: ${DEFAULT_VIEWPORT_WIDTH}.
  --viewport-height <px>  Browser viewport height. Default: ${DEFAULT_VIEWPORT_HEIGHT}.
  --vocal-decode-timeout-ms <ms>
                          Vocal FLAC decode wait timeout. Default: MIKA_VOCAL_DECODE_TIMEOUT_MS or ${DEFAULT_VOCAL_DECODE_TIMEOUT_MS}.
  --headful               Run with a visible browser window.
`);
}

function assertSmoke(condition, message, detail) {
  if (!condition) {
    const error = new Error(message);
    error.detail = detail;
    throw error;
  }
}

async function readAvatarState(page) {
  return page.evaluate(() => {
    const panel = document.querySelector("[data-mika-avatar-panel]");
    const frame = document.querySelector("[data-mika-avatar-live2d]");
    const activeButton = document.querySelector("[data-mika-avatar-choice][data-active='true']");
    const buttons = Array.from(document.querySelectorAll("[data-mika-avatar-choice]"));
    return {
      avatarId: panel?.dataset.avatarId || "",
      avatarRuntime: panel?.dataset.avatarRuntime || "",
      avatarDisplayName: panel?.dataset.avatarDisplayName || "",
      runtime: panel?.dataset.live2dRuntime || "",
      state: panel?.dataset.live2dState || "",
      modelLoaded: panel?.dataset.live2dModelLoaded || "",
      choreographyId: panel?.dataset.live2dChoreographyId || "",
      hasChoreography: panel?.dataset.live2dHasChoreography || "",
      choreographyLoadState: panel?.dataset.live2dChoreographyLoadState || "",
      choreographyTimelineLoaded: panel?.dataset.live2dChoreographyTimelineLoaded || "",
      mouthOpen: panel?.dataset.live2dMouthOpen || "",
      mouthTargetOpen: panel?.dataset.live2dMouthTargetOpen || "",
      mouthPlaying: panel?.dataset.live2dMouthPlaying || "",
      mouthShape: panel?.dataset.live2dMouthShape || "",
      externalPreview: panel?.dataset.live2dExternalPreview || "",
      avatarCatalogStatus: panel?.dataset.avatarCatalogStatus || "",
      avatarCatalogCount: panel?.dataset.avatarCatalogCount || "",
      avatarPreferredId: panel?.dataset.avatarPreferredId || "",
      avatarPreferredRuntime: panel?.dataset.avatarPreferredRuntime || "",
      avatarFormalPreviewAvailable: panel?.dataset.avatarFormalPreviewAvailable || "",
      avatarEmbedConfigStatus: panel?.dataset.avatarEmbedConfigStatus || "",
      avatarEmbedConfigAvatarId: panel?.dataset.avatarEmbedConfigAvatarId || "",
      avatarEmbedConfigRuntime: panel?.dataset.avatarEmbedConfigRuntime || "",
      avatarEmbedConfigSrc: panel?.dataset.avatarEmbedConfigSrc || "",
      avatarDefaultConnect: panel?.dataset.avatarDefaultConnect || "",
      avatarShowConnectionStatus: panel?.dataset.avatarShowConnectionStatus || "",
      avatarShowAskMika: panel?.dataset.avatarShowAskMika || "",
      avatarCanSwitchToLive2d: panel?.dataset.avatarCanSwitchToLive2d || "",
      activeChoice: activeButton?.dataset.mikaAvatarChoice || "",
      activeChoiceText: activeButton?.textContent?.trim() || "",
      choices: buttons.map(button => ({
        id: button.dataset.mikaAvatarChoice || "",
        text: button.textContent?.trim() || "",
        title: button.getAttribute("title") || ""
      })),
      frameSrc: frame?.src || "",
      hasSwitcher: Boolean(document.querySelector("[data-mika-avatar-switcher]"))
    };
  });
}

function pushRecentEvent(events, type, message) {
  events.push({
    type,
    message: String(message || "").slice(0, 800)
  });
  if (events.length > 30) {
    events.shift();
  }
}

async function waitForAvatar(page, expectedAvatarId, expectedRuntime) {
  await page.waitForFunction(
    ({ avatarId, runtime }) => {
      const panel = document.querySelector("[data-mika-avatar-panel]");
      const frame = document.querySelector("[data-mika-avatar-live2d]");
      return panel?.dataset.avatarId === avatarId
        && panel?.dataset.avatarRuntime === runtime
        && frame?.src.includes(`avatar=${avatarId}`)
        && frame?.src.includes(`runtime=${runtime}`);
    },
    { avatarId: expectedAvatarId, runtime: expectedRuntime },
    { timeout: 45000 }
  );
}

async function waitForRuntimeSeen(page, expectedRuntime) {
  await page.waitForFunction(
    (runtime) => {
      const panel = document.querySelector("[data-mika-avatar-panel]");
      return panel?.dataset.live2dRuntime === runtime
        && ["ready", "loading"].includes(panel?.dataset.live2dState || "");
    },
    expectedRuntime,
    { timeout: 45000 }
  );
}

async function waitForRuntimeState(page, expectedRuntime, expectedState, options = {}) {
  const timeout = options.timeout || 60000;
  const step = options.step || `${expectedRuntime}/${expectedState}`;
  const recentEvents = options.recentEvents || [];
  const requireModelLoaded = Boolean(options.requireModelLoaded);
  try {
    await page.waitForFunction(
      ({ runtime, state, requireModelLoaded }) => {
        const panel = document.querySelector("[data-mika-avatar-panel]");
        const runtimeReady = panel?.dataset.live2dRuntime === runtime
          && panel?.dataset.live2dState === state;
        return runtimeReady
          && (!requireModelLoaded || panel?.dataset.live2dModelLoaded === "true");
      },
      { runtime: expectedRuntime, state: expectedState, requireModelLoaded },
      { timeout }
    );
  } catch (error) {
    const state = await readAvatarState(page).catch(() => null);
    error.message = `${error.message}; step ${step}; expected ${expectedRuntime}/${expectedState}`;
    error.detail = {
      state,
      recentEvents
    };
    throw error;
  }
}

async function clickAvatar(page, avatarId, runtime) {
  await page.click(`[data-mika-avatar-choice="${avatarId}"]`);
  await waitForAvatar(page, avatarId, runtime);
  await waitForRuntimeSeen(page, runtime);
  return readAvatarState(page);
}

async function waitForMaoProMouthOnly(page) {
  await page.waitForFunction(() => {
    const data = document.querySelector("[data-mika-avatar-panel]")?.dataset;
    return data?.avatarId === "mao_pro"
      && data?.live2dRuntime === "live2d"
      && data?.live2dModelLoaded === "true"
      && data?.live2dHasChoreography === "false"
      && data?.live2dChoreographyId === ""
      && data?.live2dChoreographyLoadState === "idle"
      && data?.live2dChoreographyTimelineLoaded === "false";
  }, null, { timeout: 20000 });
}

async function waitForMouthMovement(page, expectedAvatarId, expectedRuntime, options = {}) {
  const vocalDecodeTimeoutMs = options.vocalDecodeTimeoutMs || DEFAULT_VOCAL_DECODE_TIMEOUT_MS;
  await page.evaluate(() => window.learnMoreMikaAvatar?.warmVocalBuffer?.());
  try {
    await page.waitForFunction(() => {
      const state = window.learnMoreMikaAvatar?.getVocalDecodeState?.();
      return state?.status === "ready" && state?.hasBuffer === true;
    }, null, { timeout: vocalDecodeTimeoutMs });
  } catch (error) {
    const state = await readAvatarState(page).catch(() => null);
    error.message = `${error.message}; step vocal FLAC decode; timeout ${vocalDecodeTimeoutMs}ms`;
    error.detail = { state };
    throw error;
  }
  await page.evaluate(async () => {
    if (typeof window.switchKaraokeAudioSource === "function") {
      window.switchKaraokeAudioSource("vocals");
    }
    const audio = document.getElementById("karaoke-audio-player-vocals");
    if (!audio) {
      throw new Error("vocals audio element not found");
    }
    const duration = Number(audio.duration);
    audio.currentTime = Math.max(0, Math.min(Number.isFinite(duration) ? duration - 3 : 72, 72));
    await audio.play();
  });
  await page.waitForFunction(({ expectedAvatarId, expectedRuntime }) => {
    const data = document.querySelector("[data-mika-avatar-panel]")?.dataset;
    const mouthOpen = Number(data?.live2dMouthOpen);
    const targetOpen = Number(data?.live2dMouthTargetOpen);
    return data?.avatarId === expectedAvatarId
      && data?.live2dRuntime === expectedRuntime
      && data?.live2dMouthPlaying === "true"
      && (mouthOpen > 0.025 || targetOpen > 0.025);
  }, { expectedAvatarId, expectedRuntime }, { timeout: 20000 });
}

async function main() {
  const args = parseArgs(process.argv);
  const browser = await chromium.launch({
    executablePath: args.chromePath,
    headless: !args.headful,
    args: [
      "--autoplay-policy=no-user-gesture-required",
      "--no-sandbox",
      "--enable-webgl",
      "--ignore-gpu-blocklist",
      "--use-angle=swiftshader",
      "--enable-unsafe-swiftshader"
    ]
  });

  const page = await browser.newPage({
    viewport: { width: args.viewportWidth, height: args.viewportHeight }
  });
  const recentEvents = [];
  page.on("console", (message) => {
    pushRecentEvent(recentEvents, `console:${message.type()}`, message.text());
  });
  page.on("pageerror", (error) => {
    pushRecentEvent(recentEvents, "pageerror", error.message);
  });
  page.on("requestfailed", (request) => {
    const url = request.url();
    if (url.includes("mika-avatar") || url.includes("vrm") || url.includes("blob:")) {
      pushRecentEvent(recentEvents, "requestfailed", `${request.failure()?.errorText || "failed"} ${url}`);
    }
  });

  try {
    await page.goto(args.url, { waitUntil: "domcontentloaded", timeout: 60000 });
    await page.waitForSelector("[data-mika-avatar-choice='mao_pro']", { timeout: 60000 });
    await waitForAvatar(page, "mao_pro", "live2d");
    await waitForRuntimeState(page, "live2d", "ready", {
      timeout: 60000,
      step: "mao_pro initial",
      recentEvents,
      requireModelLoaded: true
    });
    const initial = await readAvatarState(page);
    assertSmoke(initial.hasSwitcher, "Mika avatar switcher should be rendered", initial);
    assertSmoke(initial.avatarCatalogStatus === "ready", "Mika avatar catalog should load", initial);
    assertSmoke(Number(initial.avatarCatalogCount) === EXPECTED_SAMPLE_AVATARS.length, "LearnMore should expose official sample avatars only", initial);
    assertSmoke(initial.avatarFormalPreviewAvailable === "false", "Deprecated Mika preview should not be available", initial);
    assertSmoke(initial.avatarPreferredId === "mao_pro", "LearnMore should keep mao_pro as the preferred default", initial);
    assertSmoke(initial.avatarPreferredRuntime === "live2d", "LearnMore should prefer live2d runtime", initial);
    assertSmoke(initial.avatarEmbedConfigStatus === "ready", "LearnMore should load Mika embed-config endpoint", initial);
    assertSmoke(initial.avatarEmbedConfigAvatarId === "mao_pro", "Mika embed-config should select mao_pro", initial);
    assertSmoke(initial.avatarEmbedConfigRuntime === "live2d", "Mika embed-config should select live2d runtime", initial);
    assertSmoke(initial.avatarEmbedConfigSrc.includes("/mika-avatar/embed?avatar=mao_pro&runtime=live2d"), "Mika embed-config should provide mao_pro embed src", initial);
    assertSmoke(initial.avatarDefaultConnect === "true", "Mika embed-config should request default connection", initial);
    assertSmoke(initial.avatarShowConnectionStatus === "false", "Mika embed-config should hide connection status", initial);
    assertSmoke(initial.avatarShowAskMika === "false", "Mika embed-config should hide ask-Mika UI", initial);
    assertSmoke(initial.avatarCanSwitchToLive2d === "false", "Mika embed-config should keep unfinished Mika disabled", initial);
    assertSmoke(initial.activeChoice === "mao_pro", "LearnMore should default to mao_pro", initial);
    assertSmoke(initial.avatarDisplayName === "mao_pro", "mao_pro should display as mao_pro", initial);
    assertSmoke(initial.activeChoiceText === "mao_pro", "mao_pro switch label should be mao_pro", initial);
    EXPECTED_SAMPLE_AVATARS.forEach((avatar) => {
      assertSmoke(
        initial.choices.some(choice => choice.id === avatar.id && choice.text === avatar.label && choice.title === avatar.label),
        `${avatar.id} official sample choice should be rendered`,
        initial
      );
    });
    BLOCKED_LEARNMORE_AVATAR_IDS.forEach((avatarId) => {
      assertSmoke(!initial.choices.some(choice => choice.id === avatarId), `${avatarId} should not be rendered`, initial);
    });

    const sampleSwitchStates = [];
    for (const avatar of EXPECTED_SAMPLE_AVATARS.filter(item => item.id !== "mao_pro")) {
      await clickAvatar(page, avatar.id, avatar.runtime);
      await waitForRuntimeState(page, avatar.runtime, "ready", {
        timeout: 60000,
        step: `${avatar.id} switch`,
        recentEvents,
        requireModelLoaded: true
      });
      const sampleState = await readAvatarState(page);
      assertSmoke(sampleState.activeChoice === avatar.id, `${avatar.id} should become active after switching`, sampleState);
      assertSmoke(sampleState.avatarDisplayName === avatar.label, `${avatar.id} should display ${avatar.label}`, sampleState);
      sampleSwitchStates.push(sampleState);
    }

    await clickAvatar(page, "mao_pro", "live2d");
    await waitForRuntimeState(page, "live2d", "ready", {
      timeout: 60000,
      step: "mao_pro return",
      recentEvents,
      requireModelLoaded: true
    });
    const afterMaoProSwitch = await readAvatarState(page);
    assertSmoke(afterMaoProSwitch.activeChoice === "mao_pro", "mao_pro should be active after switching back", afterMaoProSwitch);
    await waitForMaoProMouthOnly(page);
    const afterMaoProMouthOnly = await readAvatarState(page);
    assertSmoke(afterMaoProMouthOnly.hasChoreography === "false", "mao_pro should not enable song choreography", afterMaoProMouthOnly);
    assertSmoke(afterMaoProMouthOnly.choreographyId === "", "mao_pro should keep choreography id empty", afterMaoProMouthOnly);
    assertSmoke(afterMaoProMouthOnly.choreographyLoadState === "idle", "mao_pro should not load a choreography timeline", afterMaoProMouthOnly);
    assertSmoke(afterMaoProMouthOnly.choreographyTimelineLoaded === "false", "mao_pro should keep choreography timeline unloaded", afterMaoProMouthOnly);

    await page.reload({ waitUntil: "domcontentloaded", timeout: 60000 });
    await page.waitForSelector("[data-mika-avatar-choice='mao_pro']", { timeout: 60000 });
    await waitForAvatar(page, "mao_pro", "live2d");
    await waitForRuntimeState(page, "live2d", "ready", {
      timeout: 60000,
      step: "mao_pro reload",
      recentEvents,
      requireModelLoaded: true
    });
    const afterReload = await readAvatarState(page);
    assertSmoke(afterReload.activeChoice === "mao_pro", "manual mao_pro choice should persist after reload", afterReload);
    assertSmoke(afterReload.runtime === "live2d", "live2d runtime status should persist after reload", afterReload);
    await waitForMaoProMouthOnly(page);
    const afterReloadMaoProMouthOnly = await readAvatarState(page);
    assertSmoke(afterReloadMaoProMouthOnly.hasChoreography === "false", "mao_pro should not enable song choreography after reload", afterReloadMaoProMouthOnly);
    await waitForMouthMovement(page, "mao_pro", "live2d", {
      vocalDecodeTimeoutMs: args.vocalDecodeTimeoutMs
    });
    const afterMaoProMouthMovement = await readAvatarState(page);
    assertSmoke(
      Number(afterMaoProMouthMovement.mouthOpen) > 0 || Number(afterMaoProMouthMovement.mouthTargetOpen) > 0,
      "mao_pro mouth should move during vocal playback",
      afterMaoProMouthMovement
    );

    console.log(JSON.stringify({
      ok: true,
      url: args.url,
      states: {
        initial,
        sampleSwitchStates,
        afterMaoProSwitch,
        afterMaoProMouthOnly,
        afterReload,
        afterReloadMaoProMouthOnly,
        afterMaoProMouthMovement
      }
    }, null, 2));
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(JSON.stringify({
    ok: false,
    message: error.message,
    detail: error.detail || null
  }, null, 2));
  process.exit(1);
});
