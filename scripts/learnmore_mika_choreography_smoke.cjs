#!/usr/bin/env node

const zlib = require("zlib");
const { chromium } = require("playwright");

const DEFAULT_URL = "https://magicplus-design.serveirc.com/LearnMore/Lyrics/ea3d8e96-fb5c-4bff-bf47-a9683e844eff";
const DEFAULT_AVATAR_BASE_URL = "http://localhost:8081/mika-avatar";
const DEFAULT_VERSION = process.env.MIKA_AVATAR_EXPECTED_VERSION || "";
const DEFAULT_VIEWPORT_WIDTH = 1600;
const DEFAULT_VIEWPORT_HEIGHT = 900;
const MIN_ADJACENT_JITTER_MEAN_DELTA = 12;
const ABRUPT_FRAME_PEAK_RATIO_MARGIN = 0.95;
const SPIKY_FRAME_ABSOLUTE_LIMIT_MARGIN = 0.95;
const ADJACENT_JITTER_CHANGED_RATIO_MARGIN = 0.75;
const ADJACENT_JITTER_PEAK_RATIO_MARGIN = 0.9;
const ADJACENT_JITTER_WITHIN_MAIN_LIMIT_MARGIN = 0.98;
const ADJACENT_JITTER_LOW_ABSOLUTE_MEAN_MARGIN = 0.9;
const ADJACENT_JITTER_LOW_ABSOLUTE_CHANGED_MARGIN = 0.9;
const MAX_COMPACT_ERROR_DETAIL_CHARS = 6000;
let fullErrorDetails = false;

const DEFAULT_SAMPLES = [
  {
    label: "verse cue",
    time: 24.2,
    expectedCue: "verse-side-step",
    expectedExpression: "1",
    expectedMotion: "mtn_03",
    expectedMotionIndex: "1",
    expectedPose: "verse"
  },
  {
    label: "pre-chorus cue",
    time: 60.7,
    expectedCue: "pre-chorus-lift-hit",
    expectedExpression: "5",
    expectedMotion: "special_01",
    expectedMotionIndex: "3",
    expectedPose: "preChorus"
  },
  {
    label: "chorus cue",
    time: 72.3,
    expectedCue: "chorus-open",
    expectedExpression: "6",
    expectedMotion: null,
    expectedMotionIndex: null,
    expectedPose: "chorus"
  },
  {
    label: "chorus call left cue",
    time: 82.4,
    expectedCue: "chorus-call-left",
    expectedExpression: "6",
    expectedMotion: null,
    expectedMotionIndex: null,
    expectedPose: "chorus"
  },
  {
    label: "chorus response right cue",
    time: 85,
    expectedCue: "chorus-response-right",
    expectedExpression: "6",
    expectedMotion: null,
    expectedMotionIndex: null,
    expectedPose: "chorus"
  },
  {
    label: "chorus heart pop cue",
    time: 95.6,
    expectedCue: "chorus-heart-pop",
    expectedExpression: "6",
    expectedMotion: null,
    expectedMotionIndex: null,
    expectedPose: "chorus"
  },
  {
    label: "second verse cue",
    time: 113.7,
    expectedCue: "second-verse-turn",
    expectedExpression: "2",
    expectedMotion: null,
    expectedMotionIndex: null,
    expectedPose: "verseB"
  },
  {
    label: "second pre-chorus sweep cue",
    time: 145.4,
    expectedCue: "second-pre-chorus-sweep",
    expectedExpression: "6",
    expectedMotion: "mtn_04",
    expectedMotionIndex: "2",
    expectedPose: "preChorusB"
  },
  {
    label: "final chorus star pop cue",
    time: 171,
    expectedCue: "final-chorus-star-pop",
    expectedExpression: "7",
    expectedMotion: "mtn_02",
    expectedMotionIndex: "0",
    expectedPose: "finalChorus"
  }
];

const CONTINUOUS_PLAYBACK_SAMPLES = [
  {
    label: "chorus continuous playback",
    time: 72,
    minTime: 72,
    maxTime: 75,
    maxMeanDelta: 16.5,
    maxChangedRatio: 0.23,
    maxPeakMeanRatio: 1.8,
    maxAdjacentMeanRatio: 2.15,
    maxVerticalMotionAbs: 1.75,
    maxSilhouetteCenterYRange: 32,
    maxSilhouetteBoundsCenterYRange: 24,
    maxSilhouetteBottomYRange: 8,
    maxMotionSwitchDelta: 2,
    minExpressionSwitchDelta: 0,
    maxExpressionSwitchDelta: 16,
    expectedStartNativeMotion: "mtn_02"
  },
  {
    label: "second verse continuous playback",
    time: 113.5,
    minTime: 113.5,
    maxTime: 116.5,
    maxMeanDelta: 16.5,
    maxChangedRatio: 0.23,
    maxPeakMeanRatio: 1.85,
    maxAdjacentMeanRatio: 2.15,
    maxVerticalMotionAbs: 1.65,
    maxSilhouetteCenterYRange: 32,
    maxSilhouetteBoundsCenterYRange: 24,
    maxSilhouetteBottomYRange: 8,
    maxMotionSwitchDelta: 2,
    maxExpressionSwitchDelta: 14,
    expectedStartNativeMotion: "mtn_02"
  },
  {
    label: "final chorus continuous playback",
    time: 150,
    minTime: 150,
    maxTime: 153,
    maxMeanDelta: 18.5,
    maxChangedRatio: 0.25,
    maxPeakMeanRatio: 1.9,
    maxAdjacentMeanRatio: 2.15,
    maxVerticalMotionAbs: 1.95,
    maxSilhouetteCenterYRange: 32,
    maxSilhouetteBoundsCenterYRange: 24,
    maxSilhouetteBottomYRange: 8,
    maxMotionSwitchDelta: 1,
    maxExpressionSwitchDelta: 14,
    expectedStartNativeMotion: "mtn_02"
  }
];

const EXPRESSION_PLAN_MIN_INTERVALS = {
  "verse-groove": 1400,
  "verse-b-groove": 2200,
  "pre-chorus-lift": 1500,
  "pre-chorus-b-lift": 2300,
  "chorus-eight-count": 9000,
  "final-chorus-eight-count": 5200
};

function parseArgs(argv) {
  const args = {
    url: DEFAULT_URL,
    expectedVersion: DEFAULT_VERSION,
    avatarBaseUrl: process.env.MIKA_AVATAR_BASE_URL || DEFAULT_AVATAR_BASE_URL,
    chromePath: process.env.CHROME_PATH || "/usr/bin/google-chrome",
    viewportWidth: DEFAULT_VIEWPORT_WIDTH,
    viewportHeight: DEFAULT_VIEWPORT_HEIGHT,
    headful: false,
    summaryOnly: false,
    summaryTable: false,
    fullErrorDetails: false
  };

  for (let i = 2; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--url" && argv[i + 1]) {
      args.url = argv[++i];
    } else if (arg === "--avatar-base-url" && argv[i + 1]) {
      args.avatarBaseUrl = argv[++i];
    } else if (arg === "--expected-version" && argv[i + 1]) {
      args.expectedVersion = argv[++i];
    } else if (arg === "--chrome-path" && argv[i + 1]) {
      args.chromePath = argv[++i];
    } else if (arg === "--viewport-width" && argv[i + 1]) {
      args.viewportWidth = parseViewportSize(argv[++i], "viewport width");
    } else if (arg === "--viewport-height" && argv[i + 1]) {
      args.viewportHeight = parseViewportSize(argv[++i], "viewport height");
    } else if (arg === "--headful") {
      args.headful = true;
    } else if (arg === "--summary-only") {
      args.summaryOnly = true;
    } else if (arg === "--summary-table") {
      args.summaryTable = true;
    } else if (arg === "--full-error-details") {
      args.fullErrorDetails = true;
    } else if (arg === "--help" || arg === "-h") {
      printHelp();
      process.exit(0);
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }

  return args;
}

function parseViewportSize(value, label) {
  const size = Number(value);
  if (!Number.isInteger(size) || size < 320 || size > 3840) {
    throw new Error(`Invalid ${label}: ${value}`);
  }
  return size;
}

function printHelp() {
  console.log(`Usage: node scripts/learnmore_mika_choreography_smoke.cjs [options]

Options:
  --url <url>                 Lyrics page to test.
  --avatar-base-url <url>     Mika avatar base URL. Default: ${DEFAULT_AVATAR_BASE_URL}.
  --expected-version <value>  Expected Mika choreography version. Default: fetch from Mika /api/version.
  --chrome-path <path>        Chrome/Chromium executable. Default: CHROME_PATH or /usr/bin/google-chrome.
  --viewport-width <px>       Browser viewport width. Default: ${DEFAULT_VIEWPORT_WIDTH}.
  --viewport-height <px>      Browser viewport height. Default: ${DEFAULT_VIEWPORT_HEIGHT}.
  --headful                   Run with a visible browser window.
  --summary-only              Print only the compact smoke summary JSON.
  --summary-table             Print a human-readable compact smoke summary table.
  --full-error-details        Print full failure details instead of compact diagnostics.
`);
}

function compactState(state) {
  if (!state || typeof state !== "object") {
    return state;
  }

  return {
    version: state.version,
    state: state.state,
    songTime: state.songTime,
    section: state.section,
    sectionPose: state.sectionPose,
    cue: state.cue,
    lastCue: state.lastCue,
    cueExpression: state.cueExpression,
    lastCueExpressionIndex: state.lastCueExpressionIndex,
    cueMotionIndex: state.cueMotionIndex,
    lastCueMotionName: state.lastCueMotionName,
    lastCueMotionIndex: state.lastCueMotionIndex,
    currentNativeMotion: state.currentNativeMotion,
    nativeMotionProfile: state.nativeMotionProfile,
    nativeMotionIntervalMs: state.nativeMotionIntervalMs,
    nativeMotionError: state.nativeMotionError,
    choreographyLoadState: state.choreographyLoadState,
    signatureLoadState: state.signatureLoadState
  };
}

function compactTelemetry(telemetry) {
  if (!telemetry || typeof telemetry !== "object") {
    return telemetry;
  }

  const choreography = telemetry.choreography || {};
  return {
    version: telemetry.version,
    songTime: telemetry.songTime,
    section: choreography.section,
    cue: choreography.cue,
    lastCue: choreography.lastCue,
    cueExpression: choreography.cueExpression,
    currentNativeMotion: choreography.currentNativeMotion,
    nativeMotionSwitches: choreography.nativeMotionSwitches,
    nativeMotionIntervalMs: choreography.nativeMotionIntervalMs,
    expressionSwitches: choreography.expressionSwitches,
    verticalMotion: choreography.verticalMotion
  };
}

function compactVisualDiffs(visualDiffs) {
  if (!visualDiffs || typeof visualDiffs !== "object") {
    return visualDiffs;
  }

  return {
    maxMeanDelta: visualDiffs.maxMeanDelta,
    maxChangedRatio: visualDiffs.maxChangedRatio,
    averageMeanDelta: visualDiffs.averageMeanDelta,
    peakMeanRatio: visualDiffs.peakMeanRatio,
    maxAdjacentMeanRatio: visualDiffs.maxAdjacentMeanRatio,
    silhouetteMotion: visualDiffs.silhouetteMotion ? {
      centerYRange: visualDiffs.silhouetteMotion.centerYRange,
      boundsCenterYRange: visualDiffs.silhouetteMotion.boundsCenterYRange,
      topYRange: visualDiffs.silhouetteMotion.topYRange,
      bottomYRange: visualDiffs.silhouetteMotion.bottomYRange
    } : null
  };
}

function compactVerticalMotion(verticalMotion) {
  if (!verticalMotion || typeof verticalMotion !== "object") {
    return verticalMotion;
  }

  return {
    sampleCount: verticalMotion.sampleCount,
    intervalMs: verticalMotion.intervalMs,
    maxAbs: verticalMotion.maxAbs
  };
}

function compactSmokeDetails(details) {
  if (!details || typeof details !== "object") {
    return details;
  }

  if (details.sample && details.state) {
    return {
      sample: details.sample,
      state: compactState(details.state)
    };
  }

  if (details.sample && details.visualDiffs) {
    return {
      sample: details.sample,
      motionSwitchDelta: details.motionSwitchDelta,
      expressionSwitchDelta: details.expressionSwitchDelta,
      visualDiffs: compactVisualDiffs(details.visualDiffs),
      verticalMotion: compactVerticalMotion(details.verticalMotion),
      expressionStart: compactTelemetry(details.expressionStart),
      expressionEnd: compactTelemetry(details.expressionEnd),
      motionStart: compactState(details.motionStart),
      motionEnd: compactState(details.motionEnd)
    };
  }

  return details;
}

function formatSmokeDetails(details) {
  if (!details) {
    return "";
  }

  const payload = fullErrorDetails ? details : compactSmokeDetails(details);
  const serialized = JSON.stringify(payload, null, 2);
  if (fullErrorDetails || serialized.length <= MAX_COMPACT_ERROR_DETAIL_CHARS) {
    return serialized;
  }

  return `${serialized.slice(0, MAX_COMPACT_ERROR_DETAIL_CHARS)}\n... truncated; rerun with --full-error-details for full diagnostics`;
}

function assertSmoke(condition, message, details = undefined) {
  if (condition) {
    return;
  }

  const formattedDetails = formatSmokeDetails(details);
  const suffix = formattedDetails ? `\n${formattedDetails}` : "";
  throw new Error(`${message}${suffix}`);
}

async function readTimelineMetadata(avatarBaseUrl, expectedVersion) {
  const url = new URL(`${avatarBaseUrl.replace(/\/$/, "")}/static/choreographies/yoasobi-idol-001.json`);
  url.searchParams.set("v", expectedVersion);
  const response = await fetch(url);
  assertSmoke(response.ok, "Mika choreography timeline fetch failed", { url: url.toString(), status: response.status });
  const data = await response.json();
  const parameters = Object.keys(data?.parameters || {});
  const microMotionParameters = [
    "ParamBodyAngleY",
    "ParamAllRotate",
    "ParamHandLA",
    "ParamHandRA",
    "ParamBrowLY",
    "ParamBrowRY",
    "ParamEyeLSmile",
    "ParamEyeRSmile"
  ];
  const missingMicroMotionParameters = microMotionParameters.filter((parameterId) => !parameters.includes(parameterId));

  return {
    choreographyId: data?.choreographyId || "",
    parameterCount: parameters.length,
    microMotionParameters,
    missingMicroMotionParameters,
    enrichmentType: data?.source?.enrichment?.at?.(-1)?.type || ""
  };
}

async function readSignatureMetadata(avatarBaseUrl, expectedVersion) {
  const url = new URL(`${avatarBaseUrl.replace(/\/$/, "")}/static/choreographies/signatures.json`);
  url.searchParams.set("v", expectedVersion);
  const response = await fetch(url);
  assertSmoke(response.ok, "Mika choreography signature fetch failed", { url: url.toString(), status: response.status });
  const data = await response.json();
  const signature = data?.signatures?.["yoasobi-idol-001"];
  const expressionPlans = signature?.expressionPlans || {};
  const planIntervals = {};

  for (const [planName, minimumIntervalMs] of Object.entries(EXPRESSION_PLAN_MIN_INTERVALS)) {
    const intervalMs = Number(expressionPlans?.[planName]?.minIntervalMs);
    planIntervals[planName] = Number.isFinite(intervalMs) ? intervalMs : null;
    assertSmoke(
      Number.isFinite(intervalMs) && intervalMs >= minimumIntervalMs,
      "Mika phrase expression interval is too short",
      { planName, intervalMs, minimumIntervalMs, url: url.toString() }
    );
  }

  return {
    choreographyId: "yoasobi-idol-001",
    planIntervals
  };
}

async function readMikaPublicVersion(avatarBaseUrl) {
  const url = new URL(`${avatarBaseUrl.replace(/\/$/, "")}/api/version`);
  const response = await fetch(url);
  assertSmoke(response.ok, "Mika version fetch failed", { url: url.toString(), status: response.status });
  const data = await response.json();
  assertSmoke(typeof data?.version === "string" && data.version.trim(), "Mika version response missing version", data);
  return data.version.trim();
}

function isIgnorableWarning(warning) {
  return warning.includes("Unrecognized feature: 'web-share'")
    || warning.includes("googleads.g.doubleclick.net")
    || warning.includes("pagead/viewthroughconversion")
    || warning.includes("target origin provided ('https://www.youtube.com')")
    || warning.includes("recipient window's origin ('https://magicplus-design.serveirc.com')");
}

function paethPredictor(left, up, upLeft) {
  const estimate = left + up - upLeft;
  const leftDistance = Math.abs(estimate - left);
  const upDistance = Math.abs(estimate - up);
  const upLeftDistance = Math.abs(estimate - upLeft);
  if (leftDistance <= upDistance && leftDistance <= upLeftDistance) {
    return left;
  }
  return upDistance <= upLeftDistance ? up : upLeft;
}

function decodePng(buffer) {
  const signature = buffer.subarray(0, 8).toString("hex");
  assertSmoke(signature === "89504e470d0a1a0a", "Screenshot is not a PNG");

  let offset = 8;
  let width = 0;
  let height = 0;
  let bitDepth = 0;
  let colorType = 0;
  const idatChunks = [];

  while (offset < buffer.length) {
    const length = buffer.readUInt32BE(offset);
    const type = buffer.toString("ascii", offset + 4, offset + 8);
    const data = buffer.subarray(offset + 8, offset + 8 + length);
    offset += length + 12;

    if (type === "IHDR") {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      bitDepth = data[8];
      colorType = data[9];
    } else if (type === "IDAT") {
      idatChunks.push(data);
    } else if (type === "IEND") {
      break;
    }
  }

  const channels = colorType === 6 ? 4 : (colorType === 2 ? 3 : 0);
  assertSmoke(bitDepth === 8 && channels > 0, "Unsupported PNG screenshot format", { bitDepth, colorType });

  const raw = zlib.inflateSync(Buffer.concat(idatChunks));
  const stride = width * channels;
  const pixels = Buffer.alloc(width * height * 4);
  let inputOffset = 0;
  let previousRow = Buffer.alloc(stride);
  let currentRow = Buffer.alloc(stride);

  for (let y = 0; y < height; y += 1) {
    const filter = raw[inputOffset++];
    for (let x = 0; x < stride; x += 1) {
      const left = x >= channels ? currentRow[x - channels] : 0;
      const up = previousRow[x] || 0;
      const upLeft = x >= channels ? previousRow[x - channels] || 0 : 0;
      let value = raw[inputOffset++];

      if (filter === 1) {
        value = (value + left) & 255;
      } else if (filter === 2) {
        value = (value + up) & 255;
      } else if (filter === 3) {
        value = (value + Math.floor((left + up) / 2)) & 255;
      } else if (filter === 4) {
        value = (value + paethPredictor(left, up, upLeft)) & 255;
      } else {
        assertSmoke(filter === 0, "Unsupported PNG filter", { filter });
      }

      currentRow[x] = value;
    }

    for (let x = 0; x < width; x += 1) {
      const source = x * channels;
      const target = (y * width + x) * 4;
      pixels[target] = currentRow[source];
      pixels[target + 1] = currentRow[source + 1];
      pixels[target + 2] = currentRow[source + 2];
      pixels[target + 3] = channels === 4 ? currentRow[source + 3] : 255;
    }

    const swap = previousRow;
    previousRow = currentRow;
    currentRow = swap;
  }

  return { width, height, pixels };
}

function compareFrames(previous, current) {
  assertSmoke(
    previous.width === current.width && previous.height === current.height,
    "Mika visual frame size changed unexpectedly",
    { previous: { width: previous.width, height: previous.height }, current: { width: current.width, height: current.height } }
  );

  let totalPixels = 0;
  let changedPixels = 0;
  let totalDelta = 0;

  for (let index = 0; index < previous.pixels.length; index += 4) {
    const alpha = Math.max(previous.pixels[index + 3], current.pixels[index + 3]);
    if (alpha < 8) {
      continue;
    }

    const delta = Math.abs(previous.pixels[index] - current.pixels[index])
      + Math.abs(previous.pixels[index + 1] - current.pixels[index + 1])
      + Math.abs(previous.pixels[index + 2] - current.pixels[index + 2]);
    totalPixels += 1;
    totalDelta += delta / 3;
    if (delta > 30) {
      changedPixels += 1;
    }
  }

  return {
    changedRatio: Number((changedPixels / Math.max(1, totalPixels)).toFixed(4)),
    meanDelta: Number((totalDelta / Math.max(1, totalPixels)).toFixed(3)),
    totalPixels
  };
}

function isLightNeutralBackgroundPixel(red, green, blue) {
  const maxChannel = Math.max(red, green, blue);
  const minChannel = Math.min(red, green, blue);
  return red >= 235 && green >= 235 && blue >= 235 && maxChannel - minChannel <= 18;
}

function measureFrameSilhouette(frame) {
  let minY = frame.height;
  let maxY = -1;
  let foregroundPixels = 0;
  let foregroundWeightTotal = 0;
  let weightedYTotal = 0;

  for (let y = 0; y < frame.height; y += 1) {
    for (let x = 0; x < frame.width; x += 1) {
      const pixelIndex = (y * frame.width + x) * 4;
      const red = frame.pixels[pixelIndex];
      const green = frame.pixels[pixelIndex + 1];
      const blue = frame.pixels[pixelIndex + 2];
      const alpha = frame.pixels[pixelIndex + 3];
      if (alpha < 16) {
        continue;
      }
      if (isLightNeutralBackgroundPixel(red, green, blue)) {
        continue;
      }

      const chroma = Math.max(red, green, blue) - Math.min(red, green, blue);
      const darkness = Math.max(0, 255 - ((red + green + blue) / 3));
      const foregroundWeight = Math.max(1, alpha * (0.35 + Math.min(1.6, (darkness + chroma) / 180)));
      foregroundPixels += 1;
      foregroundWeightTotal += foregroundWeight;
      weightedYTotal += y * foregroundWeight;
      minY = Math.min(minY, y);
      maxY = Math.max(maxY, y);
    }
  }

  assertSmoke(foregroundPixels > 800, "Mika silhouette should have enough visible foreground pixels", { foregroundPixels, width: frame.width, height: frame.height });
  const centerY = weightedYTotal / Math.max(1, foregroundWeightTotal);
  const boundsCenterY = (minY + maxY) / 2;
  return {
    centerY: Number(centerY.toFixed(3)),
    boundsCenterY: Number(boundsCenterY.toFixed(3)),
    minY,
    maxY,
    height: maxY - minY + 1,
    foregroundPixels
  };
}

function summarizeSilhouetteMotion(silhouettes) {
  const centerYs = silhouettes.map((silhouette) => silhouette.centerY);
  const boundsCenterYs = silhouettes.map((silhouette) => silhouette.boundsCenterY);
  const minYs = silhouettes.map((silhouette) => silhouette.minY);
  const maxYs = silhouettes.map((silhouette) => silhouette.maxY);
  const range = (values) => Number((Math.max(...values) - Math.min(...values)).toFixed(3));

  return {
    centerYRange: range(centerYs),
    boundsCenterYRange: range(boundsCenterYs),
    topYRange: range(minYs),
    bottomYRange: range(maxYs),
    silhouettes
  };
}

function summarizeCueSamples(samples) {
  return samples.map(({ sample, state }) => ({
    label: sample.label,
    expectedCue: sample.expectedCue,
    expectedMotion: sample.expectedMotion,
    cue: state.lastCue,
    expression: state.lastCueExpressionIndex,
    motion: state.lastCueMotionName || state.currentNativeMotion,
    sectionPose: state.sectionPose
  }));
}

function summarizeContinuousPlayback(samples) {
  return samples.map((result) => ({
    label: result.sample.label,
    motionSwitchDelta: result.motionSwitchDelta,
    maxMotionSwitchDelta: result.sample.maxMotionSwitchDelta,
    expressionSwitchDelta: result.expressionSwitchDelta,
    maxExpressionSwitchDelta: result.sample.maxExpressionSwitchDelta,
    maxMeanDelta: result.visualDiffs.maxMeanDelta,
    maxMeanDeltaLimit: result.sample.maxMeanDelta,
    maxChangedRatio: result.visualDiffs.maxChangedRatio,
    maxChangedRatioLimit: result.sample.maxChangedRatio,
    peakMeanRatio: result.visualDiffs.peakMeanRatio,
    peakMeanRatioLimit: result.sample.maxPeakMeanRatio,
    adjacentMeanRatio: result.visualDiffs.maxAdjacentMeanRatio,
    adjacentMeanRatioLimit: result.sample.maxAdjacentMeanRatio,
    adjacentMeanRatioDetail: formatAdjacentMeanRatioDetail(result.visualDiffs.maxAdjacentMeanRatioDetail),
    verticalMotionMaxAbs: result.verticalMotion.maxAbs,
    verticalMotionMaxAbsLimit: result.sample.maxVerticalMotionAbs,
    silhouetteCenterYRange: result.visualDiffs.silhouetteMotion.centerYRange,
    silhouetteCenterYRangeLimit: result.sample.maxSilhouetteCenterYRange,
    silhouetteBoundsCenterYRange: result.visualDiffs.silhouetteMotion.boundsCenterYRange,
    silhouetteBoundsCenterYRangeLimit: result.sample.maxSilhouetteBoundsCenterYRange,
    silhouetteBottomYRange: result.visualDiffs.silhouetteMotion.bottomYRange,
    silhouetteBottomYRangeLimit: result.sample.maxSilhouetteBottomYRange,
    startMotion: result.motionStart.currentNativeMotion,
    endMotion: result.motionEnd.currentNativeMotion
  }));
}

function formatAdjacentMeanRatioDetail(detail) {
  if (!detail || typeof detail !== "object") {
    return "";
  }

  const fromTime = Number.isFinite(detail.fromSongTime) ? detail.fromSongTime : "";
  const toTime = Number.isFinite(detail.toSongTime) ? detail.toSongTime : "";
  const fromCue = detail.fromCue || "";
  const toCue = detail.toCue || "";
  const cueText = fromCue || toCue ? ` ${fromCue || "(none)"}->${toCue || "(none)"}` : "";
  return `${fromTime}->${toTime}${cueText}`;
}

function formatActualLimit(actual, limit) {
  const actualText = actual === null || actual === undefined ? "" : String(actual);
  const limitText = limit === null || limit === undefined ? "" : String(limit);
  return limitText ? `${actualText} / ${limitText}` : actualText;
}

function buildSmokeSummary({
  expectedVersion,
  viewport,
  initial,
  timeline,
  samples,
  visualDiffs,
  continuousPlaybackSamples,
  nativeState,
  warnings
}) {
  return {
    expectedVersion,
    viewport,
    loadedVersion: initial.version,
    choreographyId: initial.choreographyId,
    timelineParameterCount: timeline.parameterCount,
    cueSamples: summarizeCueSamples(samples),
    cueTransitionDiffs: visualDiffs.map((diff) => ({
      from: diff.from,
      to: diff.to,
      meanDelta: diff.meanDelta,
      changedRatio: diff.changedRatio
    })),
    continuousPlayback: summarizeContinuousPlayback(continuousPlaybackSamples),
    nativeFallback: {
      hasChoreography: nativeState.hasChoreography,
      choreographyLoadState: nativeState.choreographyLoadState,
      currentNativeMotion: nativeState.currentNativeMotion
    },
    warningCount: warnings.length,
    warningSamples: warnings.slice(0, 3)
  };
}

function formatSmokeSummaryTable(output) {
  const summary = output.summary || {};
  const lines = [
    `Mika choreography smoke: ${output.ok ? "ok" : "failed"}`,
    `URL: ${output.url}`,
    `Version: ${summary.loadedVersion || ""} (expected ${summary.expectedVersion || output.expectedVersion || ""})`,
    `Viewport: ${summary.viewport?.width || ""}x${summary.viewport?.height || ""}`,
    `Choreography: ${summary.choreographyId || ""}, timeline parameters: ${summary.timelineParameterCount ?? ""}`,
    "",
    "Cue samples:",
    "label | cue | expression | motion | section",
    "--- | --- | --- | --- | ---"
  ];

  for (const cue of summary.cueSamples || []) {
    lines.push([
      cue.label,
      cue.cue,
      cue.expression,
      cue.motion,
      cue.sectionPose
    ].map((value) => value === null || value === undefined ? "" : String(value)).join(" | "));
  }

  lines.push(
    "",
    "Continuous playback:",
    "label | motion switches | expression switches | max mean delta | changed ratio | peak ratio | adjacent ratio | adjacent detail | vertical max | bottom range | start -> end",
    "--- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---"
  );

  for (const playback of summary.continuousPlayback || []) {
    lines.push([
      playback.label,
      formatActualLimit(playback.motionSwitchDelta, playback.maxMotionSwitchDelta),
      formatActualLimit(playback.expressionSwitchDelta, playback.maxExpressionSwitchDelta),
      formatActualLimit(playback.maxMeanDelta, playback.maxMeanDeltaLimit),
      formatActualLimit(playback.maxChangedRatio, playback.maxChangedRatioLimit),
      formatActualLimit(playback.peakMeanRatio, playback.peakMeanRatioLimit),
      formatActualLimit(playback.adjacentMeanRatio, playback.adjacentMeanRatioLimit),
      playback.adjacentMeanRatioDetail,
      formatActualLimit(playback.verticalMotionMaxAbs, playback.verticalMotionMaxAbsLimit),
      formatActualLimit(playback.silhouetteBottomYRange, playback.silhouetteBottomYRangeLimit),
      `${playback.startMotion || ""} -> ${playback.endMotion || ""}`
    ].map((value) => value === null || value === undefined ? "" : String(value)).join(" | "));
  }

  lines.push(
    "",
    `Native fallback: choreography=${summary.nativeFallback?.hasChoreography ?? ""}, state=${summary.nativeFallback?.choreographyLoadState ?? ""}, motion=${summary.nativeFallback?.currentNativeMotion ?? ""}`,
    `Warnings: ${summary.warningCount ?? 0}`
  );
  for (const warning of summary.warningSamples || []) {
    lines.push(`- ${String(warning).slice(0, 240)}`);
  }

  return lines.join("\n");
}

async function captureMikaFrame(page) {
  const panel = page.locator("[data-mika-avatar-panel]");
  const box = await panel.boundingBox();
  assertSmoke(Boolean(box && box.width > 80 && box.height > 120), "Mika panel should be visible before screenshot", box);
  return decodePng(await panel.screenshot({ type: "png" }));
}

async function sendYouTubeCommand(page, func, args = []) {
  await page.evaluate(({ func, args }) => {
    const iframe = document.querySelector('iframe[src*="youtube.com/embed"]');
    iframe?.contentWindow?.postMessage(JSON.stringify({ event: "command", func, args }), "*");
  }, { func, args });
}

async function seekOriginalPlayback(page, time, shouldPlay) {
  return page.evaluate(({ time, shouldPlay }) => {
    if (typeof window.seekOriginalPlayback === "function") {
      window.seekOriginalPlayback(time, shouldPlay);
      return true;
    }
    return false;
  }, { time, shouldPlay });
}

async function seekAndPlayToRange(page, time, minSeconds, maxSeconds, label) {
  let lastError = null;
  for (let attempt = 1; attempt <= 3; attempt += 1) {
    try {
      await sendYouTubeCommand(page, "mute");
      const usedPagePlayer = await seekOriginalPlayback(page, time, true);
      if (!usedPagePlayer) {
        await sendYouTubeCommand(page, "seekTo", [time, true]);
      }
      await page.waitForTimeout(300);
      if (!usedPagePlayer) {
        await sendYouTubeCommand(page, "playVideo");
      }
      await waitForSongTimeRange(page, minSeconds, maxSeconds);
      return;
    } catch (error) {
      lastError = error;
      await page.waitForTimeout(800);
    }
  }

  const state = await readMikaState(page);
  throw new Error(`${label}: YouTube playback did not enter target song time range\n${JSON.stringify({
    time,
    minSeconds,
    maxSeconds,
    state,
    lastError: lastError?.message || String(lastError || "")
  }, null, 2)}`);
}

async function readMikaState(page) {
  return page.evaluate(() => {
    const panel = document.querySelector("[data-mika-avatar-panel]");
    const data = panel?.dataset || {};
    return {
      version: data.live2dVersion || "",
      runtime: data.live2dRuntime || "",
      state: data.live2dState || "",
      modelLoaded: data.live2dModelLoaded || "",
      choreographyId: data.live2dChoreographyId || "",
      hasChoreography: data.live2dHasChoreography || "",
      choreographyLoadState: data.live2dChoreographyLoadState || "",
      choreographyTimelineLoaded: data.live2dChoreographyTimelineLoaded || "",
      signatureLoadState: data.live2dSignatureLoadState || "",
      signatureLoadError: data.live2dSignatureLoadError || "",
      songTime: data.live2dSongTime || "",
      section: data.live2dSongSection || "",
      sectionPose: data.live2dSongSectionPose || "",
      cue: data.live2dSongCue || "",
      cueProgress: data.live2dSongCueProgress || "",
      lastCue: data.live2dSongLastCue || "",
      cueExpression: data.live2dSongCueExpression || "",
      lastCueExpressionKey: data.live2dSongLastCueExpressionKey || "",
      lastCueExpressionIndex: data.live2dSongLastCueExpressionIndex || "",
      cueMotionIndex: data.live2dSongCueMotionIndex || "",
      lastCueMotionKey: data.live2dSongLastCueMotionKey || "",
      lastCueMotionName: data.live2dSongLastCueMotionName || "",
      lastCueMotionIndex: data.live2dSongLastCueMotionIndex || "",
      currentNativeMotion: data.live2dCurrentNativeMotion || "",
      nativeMotionProfile: data.live2dNativeMotionProfile || "",
      nativeMotionIntervalMs: data.live2dNativeMotionIntervalMs || "",
      nativeMotionError: data.live2dNativeMotionError || "",
      avatarBaseUrl: data.avatarBaseUrl || "",
      danceState: window.learnMoreMikaAvatar?.getDanceState?.() || null
    };
  });
}

async function waitForCueState(page, sample) {
  try {
    await page.waitForFunction((expected) => {
      const data = document.querySelector("[data-mika-avatar-panel]")?.dataset;
      if (data?.live2dRuntime === "vrm") {
        return (data?.live2dSongLastCue === expected.expectedCue || data?.live2dSongCue === expected.expectedCue)
          && data?.live2dSongSection === expected.expectedPose;
      }
      return data?.live2dSongLastCue === expected.expectedCue
        && data?.live2dSongLastCueExpressionIndex === expected.expectedExpression;
    }, sample, { timeout: 12000 });
  } catch (error) {
    const state = await readMikaState(page);
    throw new Error(`${sample.label}: timed out waiting for cue state\n${JSON.stringify({ sample, state }, null, 2)}`);
  }
}

function readSwitchCount(state) {
  const count = Number(state?.danceState?.live2d?.nativeMotionSwitches || state?.nativeMotionSwitches);
  return Number.isFinite(count) ? count : 0;
}

async function readMikaTelemetry(page) {
  return page.evaluate(() => window.__mikaLive2dTelemetry || null);
}

function readExpressionSwitchCount(telemetry) {
  const count = Number(telemetry?.choreography?.expressionSwitches);
  return Number.isFinite(count) ? count : 0;
}

function readNativeMotionIntervalMs(stateOrTelemetry) {
  const count = Number(
    stateOrTelemetry?.danceState?.live2d?.nativeMotionIntervalMs
      || stateOrTelemetry?.nativeMotionIntervalMs
      || stateOrTelemetry?.choreography?.nativeMotionIntervalMs
  );
  return Number.isFinite(count) ? count : 0;
}

function hasExpressionSwitchCount(telemetry) {
  return Number.isFinite(Number(telemetry?.choreography?.expressionSwitches));
}

function readVerticalMotionAbs(telemetry) {
  const verticalMotion = telemetry?.choreography?.verticalMotion || {};
  const values = [
    verticalMotion.total,
    verticalMotion.core,
    verticalMotion.pose,
    verticalMotion.nativeAccent,
    verticalMotion.choreographyAccent,
    verticalMotion.signatureAccent
  ].map(Number).filter(Number.isFinite);

  return values.length ? Math.max(...values.map((value) => Math.abs(value))) : null;
}

async function sampleVerticalMotion(page, sampleCount = 10, intervalMs = 500) {
  const samples = [];
  for (let index = 0; index < sampleCount; index += 1) {
    if (index > 0) {
      await page.waitForTimeout(intervalMs);
    }

    const telemetry = await readMikaTelemetry(page);
    const maxAbs = readVerticalMotionAbs(telemetry);
    samples.push({
      maxAbs,
      verticalMotion: telemetry?.choreography?.verticalMotion || null
    });
  }

  const numericValues = samples.map((sample) => sample.maxAbs).filter(Number.isFinite);
  return {
    sampleCount,
    intervalMs,
    maxAbs: numericValues.length ? Number(Math.max(...numericValues).toFixed(3)) : null,
    samples
  };
}

async function waitForSongTimeRange(page, minSeconds, maxSeconds) {
  try {
    await page.waitForFunction(({ minSeconds, maxSeconds }) => {
      const value = Number(document.querySelector("[data-mika-avatar-panel]")?.dataset.live2dSongTime);
      return Number.isFinite(value) && value >= minSeconds && value <= maxSeconds;
    }, { minSeconds, maxSeconds }, { timeout: 20000 });
  } catch (error) {
    const state = await readMikaState(page);
    throw new Error(`Timed out waiting for Mika song time range\n${JSON.stringify({ minSeconds, maxSeconds, state }, null, 2)}`);
  }
}

async function sampleContinuousVisualDiffs(page, frameCount = 12, intervalMs = 650) {
  const frames = [];
  const diffs = [];
  const silhouettes = [];
  const frameStates = [];

  for (let index = 0; index < frameCount; index += 1) {
    if (index > 0) {
      await page.waitForTimeout(intervalMs);
    }

    const frame = await captureMikaFrame(page);
    frameStates.push(await readMikaState(page));
    silhouettes.push(measureFrameSilhouette(frame));
    if (frames.length) {
      const previousState = frameStates[frameStates.length - 2] || {};
      const currentState = frameStates[frameStates.length - 1] || {};
      diffs.push({
        ...compareFrames(frames[frames.length - 1], frame),
        fromSongTime: Number(Number(previousState.songTime || 0).toFixed(3)),
        toSongTime: Number(Number(currentState.songTime || 0).toFixed(3)),
        fromCue: previousState.cue || "",
        toCue: currentState.cue || "",
        fromSection: previousState.section || "",
        toSection: currentState.section || ""
      });
    }
    frames.push(frame);
  }

  const maxMeanDelta = Math.max(...diffs.map((diff) => diff.meanDelta));
  const maxChangedRatio = Math.max(...diffs.map((diff) => diff.changedRatio));
  const averageMeanDelta = diffs.reduce((sum, diff) => sum + diff.meanDelta, 0) / Math.max(1, diffs.length);
  const meanDeltaVariance = diffs.reduce((sum, diff) => {
    const delta = diff.meanDelta - averageMeanDelta;
    return sum + delta * delta;
  }, 0) / Math.max(1, diffs.length);
  const meanDeltaStdDev = Math.sqrt(meanDeltaVariance);
  const peakMeanRatio = maxMeanDelta / Math.max(0.001, averageMeanDelta);
  const adjacentMeanRatios = diffs.map((diff, index) => {
    const neighbors = [
      diffs[index - 1]?.meanDelta,
      diffs[index + 1]?.meanDelta
    ].filter(Number.isFinite);
    if (!neighbors.length) {
      return 1;
    }
    const neighborMean = neighbors.reduce((sum, value) => sum + value, 0) / neighbors.length;
    return diff.meanDelta / Math.max(0.001, neighborMean);
  });
  const maxAdjacentMeanRatio = Math.max(...adjacentMeanRatios);
  const adjacentMeanRatioDetails = adjacentMeanRatios.map((ratio, index) => ({
    index,
    ratio: Number(ratio.toFixed(3)),
    meanDelta: diffs[index]?.meanDelta ?? null,
    fromSongTime: diffs[index]?.fromSongTime ?? null,
    toSongTime: diffs[index]?.toSongTime ?? null,
    fromCue: diffs[index]?.fromCue || "",
    toCue: diffs[index]?.toCue || "",
    fromSection: diffs[index]?.fromSection || "",
    toSection: diffs[index]?.toSection || ""
  }));
  const maxAdjacentMeanRatioDetail = adjacentMeanRatioDetails.reduce((best, item) => (
    item.ratio > (best?.ratio ?? -Infinity) ? item : best
  ), null);

  return {
    frameCount,
    intervalMs,
    maxMeanDelta: Number(maxMeanDelta.toFixed(3)),
    maxChangedRatio: Number(maxChangedRatio.toFixed(4)),
    averageMeanDelta: Number(averageMeanDelta.toFixed(3)),
    meanDeltaStdDev: Number(meanDeltaStdDev.toFixed(3)),
    peakMeanRatio: Number(peakMeanRatio.toFixed(3)),
    maxAdjacentMeanRatio: Number(maxAdjacentMeanRatio.toFixed(3)),
    maxAdjacentMeanRatioDetail,
    silhouetteMotion: summarizeSilhouetteMotion(silhouettes),
    diffs
  };
}

async function run() {
  const args = parseArgs(process.argv);
  fullErrorDetails = args.fullErrorDetails;
  const expectedVersion = args.expectedVersion || await readMikaPublicVersion(args.avatarBaseUrl);
  const browser = await chromium.launch({
    headless: !args.headful,
    executablePath: args.chromePath,
    args: [
      "--autoplay-policy=no-user-gesture-required",
      "--no-sandbox",
      "--ignore-gpu-blocklist",
      "--enable-webgl",
      "--enable-unsafe-swiftshader",
      "--use-angle=swiftshader",
      "--disable-features=UseOzonePlatform,Vulkan"
    ]
  });

  const viewport = {
    width: args.viewportWidth,
    height: args.viewportHeight
  };
  const page = await browser.newPage({ viewport });
  await page.addInitScript(() => {
    window.__mikaLive2dTelemetry = null;
    window.__mikaLive2dTelemetryMessages = [];
    window.addEventListener("message", (event) => {
      const data = event.data;
      if (!data || data.type !== "mika-live2d-status") {
        return;
      }
      window.__mikaLive2dTelemetry = data;
      window.__mikaLive2dTelemetryMessages.push({
        at: Date.now(),
        expressionSwitches: data.choreography?.expressionSwitches ?? null,
        lastExpression: data.choreography?.lastExpression ?? null,
        nativeMotionIntervalMs: data.choreography?.nativeMotionIntervalMs ?? null,
        currentNativeMotion: data.choreography?.currentNativeMotion ?? ""
      });
      if (window.__mikaLive2dTelemetryMessages.length > 200) {
        window.__mikaLive2dTelemetryMessages.shift();
      }
    });
  });
  const warnings = [];
  page.on("console", (message) => {
    if (["error", "warning"].includes(message.type())) {
      warnings.push(`${message.type()}: ${message.text()}`);
    }
  });
  page.on("pageerror", (error) => warnings.push(`pageerror: ${error.message}`));

  try {
    const url = args.url.includes("?")
      ? `${args.url}&verify=mika-choreography-smoke`
      : `${args.url}?verify=mika-choreography-smoke`;
    await page.goto(url, { waitUntil: "domcontentloaded", timeout: 60000 });
    await page.waitForSelector("[data-mika-avatar-choice='mao_pro']", { timeout: 60000 });
    await page.click("[data-mika-avatar-choice='mao_pro']");
    await page.waitForFunction((version) => {
      return document.querySelector("[data-mika-avatar-panel]")?.dataset.live2dVersion === version;
    }, expectedVersion, { timeout: 60000 });
    await page.waitForFunction(() => document.querySelector('iframe[src*="youtube.com/embed"]'), null, { timeout: 60000 });
    await page.waitForFunction(() => {
      const data = document.querySelector("[data-mika-avatar-panel]")?.dataset;
      return data?.live2dChoreographyLoadState === "ready"
        && data?.live2dModelLoaded === "true";
    }, null, { timeout: 60000 });

    const initial = await readMikaState(page);
    assertSmoke(initial.version === expectedVersion, "Mika version mismatch", initial);
    assertSmoke(initial.state === "ready", "Mika Live2D should be ready", initial);
    assertSmoke(initial.modelLoaded === "true", "Mika model should be loaded", initial);
    assertSmoke(initial.choreographyId === "yoasobi-idol-001", "Mika should load YOASOBI Idol choreography", initial);
    assertSmoke(initial.choreographyTimelineLoaded === "true", "Mika choreography timeline should be loaded", initial);
    assertSmoke(initial.signatureLoadState === "ready", "Mika signature should be ready", initial);
    const isVrmRuntime = initial.runtime === "vrm";
    const timeline = await readTimelineMetadata(initial.avatarBaseUrl || args.avatarBaseUrl, expectedVersion);
    assertSmoke(timeline.choreographyId === "yoasobi-idol-001", "Mika timeline choreography id mismatch", timeline);
    assertSmoke(timeline.parameterCount >= 22, "Mika timeline should include enriched micro-motion parameters", timeline);
    assertSmoke(timeline.missingMicroMotionParameters.length === 0, "Mika timeline missing micro-motion parameters", timeline);
    assertSmoke(timeline.enrichmentType === "mika-micro-motion", "Mika timeline enrichment metadata mismatch", timeline);
    const signature = await readSignatureMetadata(initial.avatarBaseUrl || args.avatarBaseUrl, expectedVersion);

    const samples = [];
    const visualDiffs = [];
    let previousFrame = null;
    let previousSample = null;
    for (const sample of DEFAULT_SAMPLES) {
      await seekAndPlayToRange(page, sample.time, sample.time, sample.time + 2.2, sample.label);
      await waitForCueState(page, sample);
      await page.waitForTimeout(isVrmRuntime ? 180 : 1500);

      const state = await readMikaState(page);
      const cueProgress = Number(state.cueProgress);
      assertSmoke(
        state.lastCue === sample.expectedCue || state.cue === sample.expectedCue,
        `${sample.label}: cue mismatch`,
        state
      );
      if (!isVrmRuntime) {
        assertSmoke(state.lastCueExpressionIndex === sample.expectedExpression, `${sample.label}: cue expression mismatch`, state);
      }
      if (!isVrmRuntime && sample.expectedMotion) {
        assertSmoke(
          state.currentNativeMotion === sample.expectedMotion || state.lastCueMotionName === sample.expectedMotion,
          `${sample.label}: cue motion mismatch`,
          { sample, state }
        );
        assertSmoke(
          state.lastCueMotionIndex === sample.expectedMotionIndex || state.currentNativeMotion === sample.expectedMotion,
          `${sample.label}: cue motion index mismatch`,
          { sample, state }
        );
      } else if (!isVrmRuntime) {
        assertSmoke(
          !String(state.lastCueMotionKey || "").startsWith(`${sample.expectedCue}:`),
          `${sample.label}: parameter-only cue should not restart native motion`,
          { sample, state }
        );
      }
      if (!isVrmRuntime) {
        assertSmoke(/^(mtn_01|mtn_02|mtn_03|mtn_04|special_01|special_02|special_03)$/.test(state.currentNativeMotion), `${sample.label}: current native motion missing`, state);
      }
      assertSmoke(
        (isVrmRuntime ? state.section : state.sectionPose) === sample.expectedPose,
        `${sample.label}: section pose mismatch`,
        state
      );
      assertSmoke(!state.nativeMotionError, `${sample.label}: native motion error`, state);
      assertSmoke(!Number.isFinite(cueProgress) || (cueProgress >= 0 && cueProgress <= 1), `${sample.label}: cue progress out of range`, state);

      const frame = await captureMikaFrame(page);
      if (previousFrame) {
        visualDiffs.push({
          from: previousSample.label,
          to: sample.label,
          ...compareFrames(previousFrame, frame)
        });
      }
      previousFrame = frame;
      previousSample = sample;
      samples.push({ sample, state });
    }

    const meaningfulVisualDiffs = visualDiffs.filter((diff) => diff.changedRatio >= 0.02 && diff.meanDelta >= 2.5);
    assertSmoke(
      meaningfulVisualDiffs.length >= Math.max(3, Math.floor(visualDiffs.length * 0.65)),
      "Mika rendered frames did not change enough across choreography cues",
      { visualDiffs }
    );

    const continuousPlaybackSamples = [];
    for (const sample of CONTINUOUS_PLAYBACK_SAMPLES) {
      await page.evaluate(() => window.learnMoreMikaAvatar?.refreshSongProfile?.(null));
      await seekAndPlayToRange(page, sample.time, sample.minTime, sample.maxTime, sample.label);
      await page.waitForTimeout(1000);
      const motionStart = await readMikaState(page);
      const expressionStart = await readMikaTelemetry(page);
      const visualDiffsDuringPlayback = await sampleContinuousVisualDiffs(page);
      const verticalMotionDuringPlayback = await sampleVerticalMotion(page, 10, 500);
      await page.waitForTimeout(11000);
      const motionEnd = await readMikaState(page);
      const expressionEnd = await readMikaTelemetry(page);
      const motionSwitchDelta = readSwitchCount(motionEnd) - readSwitchCount(motionStart);
      const expressionSwitchDelta = readExpressionSwitchCount(expressionEnd) - readExpressionSwitchCount(expressionStart);
      const result = {
        sample,
        motionSwitchDelta,
        expressionSwitchDelta,
        visualDiffs: visualDiffsDuringPlayback,
        expressionStart,
        expressionEnd,
        verticalMotion: verticalMotionDuringPlayback,
        motionStart,
        motionEnd
      };
      continuousPlaybackSamples.push(result);
      if (!isVrmRuntime) {
        assertSmoke(
          hasExpressionSwitchCount(expressionStart)
            && hasExpressionSwitchCount(expressionEnd)
            && expressionSwitchDelta >= (sample.minExpressionSwitchDelta ?? 1),
          `${sample.label}: Mika expression telemetry missing`,
          result
        );
        assertSmoke(
          motionStart.currentNativeMotion === sample.expectedStartNativeMotion,
          `${sample.label}: Mika kept stale native motion after seek`,
          result
        );
        assertSmoke(
          readNativeMotionIntervalMs(motionStart) === 0 || readNativeMotionIntervalMs(motionStart) >= 1200,
          `${sample.label}: Mika reported stale native motion interval after seek`,
          result
        );
        assertSmoke(
          motionSwitchDelta <= sample.maxMotionSwitchDelta,
          `${sample.label}: Mika native motions switched too often`,
          result
        );
        assertSmoke(
          expressionSwitchDelta <= sample.maxExpressionSwitchDelta,
          `${sample.label}: Mika expressions switched too often`,
          result
        );
      }
      assertSmoke(
        visualDiffsDuringPlayback.maxMeanDelta <= sample.maxMeanDelta
          || visualDiffsDuringPlayback.maxChangedRatio <= sample.maxChangedRatio
          || (
            visualDiffsDuringPlayback.peakMeanRatio < sample.maxPeakMeanRatio * ABRUPT_FRAME_PEAK_RATIO_MARGIN
            && visualDiffsDuringPlayback.maxAdjacentMeanRatio <= sample.maxAdjacentMeanRatio
          ),
        `${sample.label}: Mika frames changed too abruptly`,
        result
      );
      assertSmoke(
        visualDiffsDuringPlayback.peakMeanRatio <= sample.maxPeakMeanRatio
          || (
            visualDiffsDuringPlayback.maxMeanDelta < sample.maxMeanDelta * SPIKY_FRAME_ABSOLUTE_LIMIT_MARGIN
            && visualDiffsDuringPlayback.maxChangedRatio < sample.maxChangedRatio * SPIKY_FRAME_ABSOLUTE_LIMIT_MARGIN
          ),
        `${sample.label}: Mika frame changes were too spiky`,
        result
      );
      assertSmoke(
        visualDiffsDuringPlayback.maxMeanDelta < MIN_ADJACENT_JITTER_MEAN_DELTA
          || visualDiffsDuringPlayback.maxChangedRatio < sample.maxChangedRatio * ADJACENT_JITTER_CHANGED_RATIO_MARGIN
          || visualDiffsDuringPlayback.peakMeanRatio < sample.maxPeakMeanRatio * ADJACENT_JITTER_PEAK_RATIO_MARGIN
          || (
            visualDiffsDuringPlayback.maxMeanDelta <= sample.maxMeanDelta * ADJACENT_JITTER_WITHIN_MAIN_LIMIT_MARGIN
            && visualDiffsDuringPlayback.maxChangedRatio <= sample.maxChangedRatio * ADJACENT_JITTER_WITHIN_MAIN_LIMIT_MARGIN
            && visualDiffsDuringPlayback.peakMeanRatio <= sample.maxPeakMeanRatio * ADJACENT_JITTER_WITHIN_MAIN_LIMIT_MARGIN
          )
          || (
            visualDiffsDuringPlayback.maxMeanDelta <= sample.maxMeanDelta * ADJACENT_JITTER_LOW_ABSOLUTE_MEAN_MARGIN
            && visualDiffsDuringPlayback.maxChangedRatio <= sample.maxChangedRatio * ADJACENT_JITTER_LOW_ABSOLUTE_CHANGED_MARGIN
          )
          || visualDiffsDuringPlayback.maxAdjacentMeanRatio <= sample.maxAdjacentMeanRatio,
        `${sample.label}: Mika frame changes had a local jitter spike`,
        result
      );
      assertSmoke(
        visualDiffsDuringPlayback.silhouetteMotion.bottomYRange <= sample.maxSilhouetteBottomYRange,
        `${sample.label}: Mika silhouette moved vertically too much`,
        result
      );
      if (!isVrmRuntime) {
        assertSmoke(
          Number.isFinite(verticalMotionDuringPlayback.maxAbs)
            && verticalMotionDuringPlayback.maxAbs <= sample.maxVerticalMotionAbs,
          `${sample.label}: Mika vertical motion is too large`,
          result
        );
      }
    }

    const nativeProfile = await page.evaluate(() => {
      return window.learnMoreMikaAvatar?.refreshSongProfile?.({
        songUid: "mika-smoke-native-song",
        title: "普通の歌",
        artist: "Smoke Artist",
        performer: ""
      }) || null;
    });
    assertSmoke(nativeProfile?.hasChoreography === false, "Native smoke profile should not have choreography", nativeProfile);
    await page.waitForFunction(() => {
      const data = document.querySelector("[data-mika-avatar-panel]")?.dataset;
      const runtime = data?.live2dRuntime || "";
      const nativeMotionReady = runtime === "vrm"
        ? true
        : /^(mtn_02|mtn_03|mtn_04)$/.test(data?.live2dCurrentNativeMotion || "");
      return data?.live2dHasChoreography === "false"
        && data?.live2dChoreographyId === ""
        && data?.live2dChoreographyLoadState === "idle"
        && data?.live2dChoreographyTimelineLoaded === "false"
        && nativeMotionReady;
    }, null, { timeout: 12000 });
    const nativeState = await readMikaState(page);
    assertSmoke(nativeState.hasChoreography === "false", "Mika should disable choreography for unmatched songs", nativeState);
    assertSmoke(nativeState.choreographyId === "", "Mika should clear choreography id for unmatched songs", nativeState);
    assertSmoke(nativeState.choreographyLoadState === "idle", "Mika should not keep Idol timeline loaded for unmatched songs", nativeState);
    if (!isVrmRuntime) {
      assertSmoke(/^(mtn_02|mtn_03|mtn_04)$/.test(nativeState.currentNativeMotion), "Mika should return to native motion playlist for unmatched songs", nativeState);
    }

    const filteredWarnings = warnings.filter((warning) => !isIgnorableWarning(warning));
    const summary = buildSmokeSummary({
      expectedVersion,
      viewport,
      initial,
      timeline,
      samples,
      visualDiffs,
      continuousPlaybackSamples,
      nativeState,
      warnings: filteredWarnings
    });

    const output = {
      ok: true,
      url: args.url,
      expectedVersion,
      summary,
      initial,
      timeline,
      signature,
      samples,
      visualDiffs,
      continuousPlaybackSamples,
      nativeProfile,
      nativeState,
      warnings: filteredWarnings
    };

    const compactOutput = {
      ok: output.ok,
      url: output.url,
      expectedVersion: output.expectedVersion,
      summary: output.summary,
      warnings: output.warnings
    };

    if (args.summaryTable) {
      console.log(formatSmokeSummaryTable(compactOutput));
    } else {
      console.log(JSON.stringify(args.summaryOnly ? compactOutput : output, null, 2));
    }
  } finally {
    await browser.close();
  }
}

run().catch((error) => {
  console.error(error.message);
  process.exit(1);
});
