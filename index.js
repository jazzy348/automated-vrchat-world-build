// I've pulled this code out of our build pipeline which uses workers and queues, so some of this might seem confusing.
// TL:DR our workers that run Unity are dedicated to specific platforms (PC, Android) and receive jobs via our queue's API.

const path = require("path");
const fs = require("fs");
const { spawn } = require("child_process");
const os = require("os");

// ----------------------------
// CONFIG
// ----------------------------
const PLATFORM = "pc";  // "pc" or "android"
const UNITY_EXECUTE_METHOD = "AutoVRCUploader.UploadWorldCLI";
const UPLOAD_CODE = path.join(process.cwd(), "AutoVRCUploader.cs");
const UNITY_LOGFILE = path.join(process.cwd(), "unity_upload.log");
const GIT_REMOTE = "origin";
const GIT_BRANCH = "HEAD";

const UNITY_PATH = "C:\\Program Files\\Unity\\Hub\\Editor\\2022.3.22f1\\Editor\\Unity.exe";

const PROJECT_PATH = "F:\\My_VRC_World";


// Job (This is typically handled by our job manager, hardcoding as an example)

const job = {
  "scene": "Assets/Scenes/main.unity",
  "thumbnail": "Assets/Scenes/Thumbnail.png",
  "worldName": "My Home World",
  "worldId" : "wrld_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "platform": PLATFORM,
  "commitHash": null
}


// Watchdog configuration
const MAX_IDLE_MS_DEFAULT = (15 * 60 * 1000); // 15m default
const UNITY_WATCHDOG_POLL_MS = 30 * 1000;

// Shader compilation patterns (indicates Unity is busy but healthy, this probably isn't going to work too well)
const SHADER_PATTERNS = [
  /Compiling Shaders/i,
  /shader compile/i,
  /begin compiling/i,
  /warmup/i,
  /shaderworker/i,
  /Preparing shaders/i
];

// Move into the project path so git can run
process.chdir(PROJECT_PATH);

// ----------------------------
// Delay helper
// ----------------------------
function wait(ms) {
  return new Promise(res => setTimeout(res, ms));
}

function runCommand(cmd, args, opts = {}) {
  return new Promise((resolve, reject) => {
    const p = spawn(cmd, args, Object.assign({ stdio: ["ignore", "pipe", "pipe"] }, opts));
    let out = "", err = "";

    p.stdout.on("data", d => {
      const s = d.toString();
      out += s;
      process.stdout.write(s);
    });
    p.stderr.on("data", d => {
      const s = d.toString();
      err += s;
      process.stderr.write(s);
    });

    p.on("close", code => {
      if (code === 0) resolve({ out, err });
      else reject({ code, out, err });
    });
    p.on("error", reject);
  });
}

// ----------------------------
// Git operations
// ----------------------------
async function gitPull(commitHash = null) {
  console.log("Git: pulling latest...");
  const retries = 3;

  for (let i = 1; i <= retries; i++) {
    try {
      await runCommand("git", ["stash"]);
      await runCommand("git", ["fetch", GIT_REMOTE]);

      if (commitHash) {
        await runCommand("git", ["checkout", commitHash]);
      } else {
        await runCommand("git", ["checkout", GIT_BRANCH]);
        await runCommand("git", ["reset", "--hard", `${GIT_REMOTE}/${GIT_BRANCH}`]);
      }

      console.log("Git synced.");
      return;
    } catch (e) {
      console.error(`Git attempt ${i} failed:`, e);
      if (i === retries) throw e;
      await new Promise(r => setTimeout(r, 5000));
    }
  }
}

// =============================================================
// Run Unity with watchdog
// =============================================================
async function runUnityJob(job) {
  try {
    if (!fs.existsSync(PROJECT_PATH + "/Assets/Editor/")) {
      fs.mkdirSync(PROJECT_PATH + "/Assets/Editor/");
    }
    fs.copyFileSync(UPLOAD_CODE, PROJECT_PATH + "/Assets/Editor/AutoVRCUploader.cs");
  } catch (e) {
    // Non-fatal if copy fails, but log
    console.warn("Failed to copy upload helper code:", e);
  }

  const unityArgs = [
    "-projectPath", PROJECT_PATH,
    "-executeMethod", UNITY_EXECUTE_METHOD,
    "-logFile", UNITY_LOGFILE,
    "--",
    `--scene=${job.scene}`,
    `--thumbnail=${job.thumbnail}`,
    `--name=${job.worldName}`,
    `--id=${job.worldId}`,
    `--platform=${job.platform}`
  ];

  console.log(`Launching Unity for job ${job.worldName}`);

  return new Promise(async resolve => {
    const unity = spawn(UNITY_PATH, unityArgs, {
      cwd: PROJECT_PATH,
      shell: false,
      stdio: ["ignore", "pipe", "pipe"]
    });

    let lastLogTs = Date.now();

    // Watchdog: detect stalled Unity processes
    const watchdog = setInterval(async () => {
      const idleMs = Date.now() - lastLogTs;
      if (idleMs > MAX_IDLE_MS_DEFAULT) {
        // Check if Unity is busy with shader compilation
        let log = "";
        try { 
          log = fs.readFileSync(UNITY_LOGFILE, "utf8"); 
        } catch (e) { 
          /* ignore */ 
        }

        const hasShader = SHADER_PATTERNS.some(rx => rx.test(log));
        if (hasShader) {
          // Unity is busy compiling - extend watchdog
          console.log(`Unity appears to be busy compiling shaders (ignoring idle).`);
          lastLogTs = Date.now();
          return;
        }

        console.log(`Unity idle for ${idleMs}ms — killing process to restart.`);
        try { 
          unity.kill("SIGKILL"); 
        } catch (e) { 
          console.warn("kill failed:", e); 
        }
      }
    }, UNITY_WATCHDOG_POLL_MS);

    unity.on("error", e => {
      clearInterval(watchdog);
      console.error("Unity process error:", e);
    });

    unity.on("close", async code => {
      clearInterval(watchdog);

      // Read full log file one final time
      try { 
        unityLog = fs.readFileSync(UNITY_LOGFILE, "utf8"); 
      } catch (e) { 
        /* use what we collected */ 
      }

      console.log(`Unity exited with code ${code}`);
      const success = code === 0;

      console.log(job.worldName + `Unity exited with code ${code}`);
      resolve();
    });
  });
}

// =============================================================
// MAIN LOOP
// =============================================================
(async () => {

  await gitPull(job.commitHash);
  await runUnityJob(job);

})();