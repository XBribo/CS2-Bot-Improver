const dropzone = document.getElementById("dropzone");
const fileInput = document.getElementById("fileInput");
const runtimeStatus = document.getElementById("runtimeStatus");
const phase = document.getElementById("phase");
const progress = document.getElementById("progress");
const progressText = document.getElementById("progressText");
const log = document.getElementById("log");
const resultCard = document.getElementById("resultCard");
const summary = document.getElementById("summary");
const downloadLink = document.getElementById("downloadLink");
const tickrateInput = document.getElementById("tickrate");
const downsampleInput = document.getElementById("downsample");
const maxRoundSecondsInput = document.getElementById("maxRoundSeconds");

let worker;
let outputUrl;

if ("serviceWorker" in navigator) {
  navigator.serviceWorker.register("./sw.js").catch(() => {});
}

dropzone.addEventListener("dragover", event => {
  event.preventDefault();
  dropzone.classList.add("dragover");
});

dropzone.addEventListener("dragleave", () => {
  dropzone.classList.remove("dragover");
});

dropzone.addEventListener("drop", event => {
  event.preventDefault();
  dropzone.classList.remove("dragover");
  const file = event.dataTransfer && event.dataTransfer.files ? event.dataTransfer.files[0] : null;
  if (file) {
    startConversion(file);
  }
});

fileInput.addEventListener("change", () => {
  const file = fileInput.files ? fileInput.files[0] : null;
  if (file) {
    startConversion(file);
  }
});

function startConversion(file) {
  if (!file.name.toLowerCase().endsWith(".dem")) {
    writeLog(`Refusing ${file.name}: expected a .dem file`, true);
    return;
  }

  if (worker) {
    worker.terminate();
  }
  if (outputUrl) {
    URL.revokeObjectURL(outputUrl);
    outputUrl = undefined;
  }

  resultCard.hidden = true;
  log.textContent = "";
  setStatus("working");
  setProgress("Starting worker", 0, 1);

  worker = new Worker("assets/converter-worker.js");
  worker.onmessage = event => handleWorkerMessage(event.data);
  worker.onerror = event => {
    setStatus("error");
    writeLog(event.message || "Worker crashed", true);
  };

  worker.postMessage({
    type: "convert",
    file,
    options: {
      tickrate: readPositiveInt(tickrateInput.value, 64),
      downsample: readPositiveInt(downsampleInput.value, 4),
      maxRoundSeconds: readPositiveInt(maxRoundSecondsInput.value, 115)
    }
  });
}

function handleWorkerMessage(message) {
  switch (message.type) {
    case "ready":
      setStatus("ready");
      break;
    case "log":
      writeLog(message.message);
      break;
    case "progress":
      setProgress(message.phase, message.current, message.total, message.unit || "ticks");
      break;
    case "result":
      setStatus("done");
      setProgress("Done", message.stats.parsedTicks, message.stats.parsedTicks || 1, "ticks");
      showResult(message);
      break;
    case "error":
      setStatus("error");
      writeLog(message.message, true);
      break;
  }
}

function showResult(message) {
  const blob = new Blob([message.zip], { type: "application/zip" });
  outputUrl = URL.createObjectURL(blob);
  downloadLink.href = outputUrl;
  downloadLink.download = message.fileName;
  summary.textContent =
    `${message.stats.rounds} rounds, ${message.stats.entries} replay entries, ` +
    `${message.stats.parsedTicks.toLocaleString()} parsed ticks, ` +
    `${message.stats.tickRows.toLocaleString()} player-tick rows, ` +
    `${formatBytes(message.zip.byteLength)} zip.`;
  resultCard.hidden = false;
}

function setStatus(value) {
  runtimeStatus.textContent = value;
}

function setProgress(label, current, total, unit = "ticks") {
  const safeTotal = Math.max(1, Number(total) || 1);
  const safeCurrent = Math.max(0, Math.min(safeTotal, Number(current) || 0));
  phase.textContent = label || "Working";
  progress.max = safeTotal;
  progress.value = safeCurrent;
  progressText.textContent = `${safeCurrent.toLocaleString()} / ${safeTotal.toLocaleString()} ${unit}`;
}

function writeLog(message, isError = false) {
  const line = document.createElement("div");
  line.textContent = message;
  if (isError) {
    line.className = "error";
  }
  log.appendChild(line);
  log.scrollTop = log.scrollHeight;
}

function readPositiveInt(value, fallback) {
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MiB`;
}
