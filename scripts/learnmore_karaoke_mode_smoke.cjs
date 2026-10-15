#!/usr/bin/env node

const { chromium } = require("playwright");

const DEFAULT_URL = "https://magicplus-design.serveirc.com/LearnMore/Lyrics/cf66edb3-e8f3-4192-8016-68a24fe35e29";

function parseArgs(argv) {
  const args = {
    url: DEFAULT_URL,
    seekSeconds: 70,
    chromePath: process.env.CHROME_PATH || "/usr/bin/google-chrome",
    headful: false
  };

  for (let i = 2; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--url" && argv[i + 1]) {
      args.url = argv[++i];
    } else if (arg === "--seek-seconds" && argv[i + 1]) {
      args.seekSeconds = Number(argv[++i]);
    } else if (arg === "--chrome-path" && argv[i + 1]) {
      args.chromePath = argv[++i];
    } else if (arg === "--headful") {
      args.headful = true;
    } else if (arg === "--help" || arg === "-h") {
      printHelp();
      process.exit(0);
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }

  if (!Number.isFinite(args.seekSeconds) || args.seekSeconds <= 0) {
    throw new Error("--seek-seconds must be a positive number");
  }

  return args;
}

function printHelp() {
  console.log(`Usage: node scripts/learnmore_karaoke_mode_smoke.cjs [options]

Options:
  --url <url>               Lyrics page to test.
  --seek-seconds <seconds>  Progress seek target. Default: 70.
  --chrome-path <path>      Chrome/Chromium executable. Default: CHROME_PATH or /usr/bin/google-chrome.
  --headful                 Run with a visible browser window.
`);
}

function assertSmoke(condition, message, details = undefined) {
  if (condition) {
    return;
  }

  const suffix = details ? `\n${JSON.stringify(details, null, 2)}` : "";
  throw new Error(`${message}${suffix}`);
}

async function readState(page, activeKind = "instrumental") {
  return page.evaluate((kind) => {
    const audio = document.querySelector(`#karaoke-audio-player-${kind}`);
    return {
      title: document.querySelector("h1")?.textContent?.trim() || "",
      label: document.querySelector("#karaoke-audio-label-text")?.textContent?.trim() || "",
      activeButton: [...document.querySelectorAll(".karaoke-source-button.active")].map((element) => element.textContent.trim()),
      hasPanel: Boolean(document.querySelector("#karaoke-audio-panel")),
      hasInstrumental: Boolean(document.querySelector("#karaoke-audio-player-instrumental")?.src),
      hasVocals: Boolean(document.querySelector("#karaoke-audio-player-vocals")?.src),
      ytState: youtubePlayer?.getPlayerState?.(),
      ytTime: youtubePlayer?.getCurrentTime?.() || 0,
      playbackTime: typeof getPlaybackTime === "function" ? getPlaybackTime() : 0,
      audioPaused: audio?.paused,
      audioTime: audio?.currentTime || 0,
      gap: Math.abs((audio?.currentTime || 0) - (youtubePlayer?.getCurrentTime?.() || 0)),
      instrumentalPaused: document.querySelector("#karaoke-audio-player-instrumental")?.paused,
      vocalsPaused: document.querySelector("#karaoke-audio-player-vocals")?.paused,
      highlighted: document.querySelector(".lyrics-line.highlight .jp-line")?.textContent?.trim() || ""
    };
  }, activeKind);
}

async function run() {
  const args = parseArgs(process.argv);
  const browser = await chromium.launch({
    headless: !args.headful,
    executablePath: args.chromePath,
    args: ["--autoplay-policy=no-user-gesture-required", "--no-sandbox"]
  });

  const page = await browser.newPage({ viewport: { width: 1365, height: 900 } });
  const warnings = [];
  page.on("console", (message) => {
    if (["error", "warning"].includes(message.type())) {
      warnings.push(`${message.type()}: ${message.text()}`);
    }
  });
  page.on("pageerror", (error) => warnings.push(`pageerror: ${error.message}`));

  try {
    await page.goto(args.url, { waitUntil: "domcontentloaded", timeout: 30000 });
    await page.waitForSelector("#karaoke-audio-panel", { timeout: 15000 });
    await page.waitForFunction(
      () => typeof youtubePlayer !== "undefined" && youtubePlayer && typeof youtubePlayer.getPlayerState === "function",
      null,
      { timeout: 20000 }
    );

    const initial = await readState(page);
    assertSmoke(initial.hasPanel, "Karaoke audio panel is missing", initial);
    assertSmoke(initial.hasInstrumental, "Instrumental stem is missing", initial);
    assertSmoke(initial.hasVocals, "Vocal stem is missing", initial);
    assertSmoke(initial.label === "原曲模式", "Initial mode should be 原曲模式", initial);
    assertSmoke(initial.activeButton.includes("原曲"), "Initial active mode should be 原曲", initial);

    await page.evaluate(() => youtubePlayer.playVideo());
    await page.waitForTimeout(2500);
    const original = await readState(page);
    assertSmoke(original.ytState === 1, "Original mode should play YouTube", original);
    assertSmoke(Math.abs(original.playbackTime - original.ytTime) < 0.5, "Original mode playback time should come from YouTube", original);

    await page.evaluate(() => switchKaraokeAudioSource("instrumental"));
    await page.waitForTimeout(2500);
    const instrumental = await readState(page, "instrumental");
    assertSmoke(instrumental.label === "伴奏模式", "Mode should switch to 伴奏模式", instrumental);
    assertSmoke(instrumental.activeButton.includes("伴奏"), "Active mode should be 伴奏", instrumental);
    assertSmoke(instrumental.audioPaused === false, "Instrumental audio should be playing", instrumental);
    assertSmoke(instrumental.gap < 0.6, "Instrumental and YouTube time should stay synced", instrumental);

    await page.evaluate((seconds) => youtubePlayer.seekTo(seconds, true), args.seekSeconds);
    await page.waitForTimeout(1800);
    const afterSeek = await readState(page, "instrumental");
    assertSmoke(afterSeek.audioPaused === false, "Instrumental audio should keep playing after progress seek", afterSeek);
    assertSmoke(Math.abs(afterSeek.ytTime - args.seekSeconds) < 2.5, "YouTube should seek to requested progress", afterSeek);
    assertSmoke(Math.abs(afterSeek.audioTime - args.seekSeconds) < 2.5, "Instrumental audio should follow requested progress", afterSeek);
    assertSmoke(afterSeek.gap < 0.6, "Instrumental and YouTube time should stay synced after progress seek", afterSeek);
    assertSmoke(Boolean(afterSeek.highlighted), "Lyrics should remain highlighted after progress seek", afterSeek);

    await page.evaluate(() => switchKaraokeAudioSource("vocals"));
    await page.waitForTimeout(1800);
    const vocals = await readState(page, "vocals");
    assertSmoke(vocals.label === "人聲模式", "Mode should switch to 人聲模式", vocals);
    assertSmoke(vocals.activeButton.includes("人聲"), "Active mode should be 人聲", vocals);
    assertSmoke(vocals.audioPaused === false, "Vocal audio should be playing", vocals);
    assertSmoke(vocals.gap < 0.6, "Vocal and YouTube time should stay synced", vocals);

    await page.evaluate(() => switchKaraokeAudioSource("normal"));
    await page.waitForTimeout(1200);
    const normal = await readState(page, "instrumental");
    assertSmoke(normal.label === "原曲模式", "Mode should switch back to 原曲模式", normal);
    assertSmoke(normal.activeButton.includes("原曲"), "Active mode should be 原曲 after switching back", normal);
    assertSmoke(normal.instrumentalPaused === true, "Instrumental audio should pause after returning to original", normal);
    assertSmoke(normal.vocalsPaused === true, "Vocal audio should pause after returning to original", normal);

    console.log(JSON.stringify({
      ok: true,
      url: args.url,
      seekSeconds: args.seekSeconds,
      states: { initial, original, instrumental, afterSeek, vocals, normal },
      warnings: warnings.filter((warning) => !warning.includes("Unrecognized feature: 'web-share'"))
    }, null, 2));
  } finally {
    await browser.close();
  }
}

run().catch((error) => {
  console.error(error.message);
  process.exit(1);
});
