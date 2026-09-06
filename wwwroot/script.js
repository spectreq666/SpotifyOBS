const isApp = window.IS_APP_MODE || (new URLSearchParams(window.location.search).get("mode") === "app");

document.documentElement.className = isApp ? "mode-app" : "mode-obs";
if (document.body) {
    document.body.className = isApp ? "mode-app" : "mode-obs";
} else {
    document.addEventListener("DOMContentLoaded", () => {
        document.body.className = isApp ? "mode-app" : "mode-obs";
    });
}

const THEME_NAMES = {
    "neon-pink": "💗 Neon Cyber Pink",
    "deep-purple": "💜 Deep Purple Glass",
    "emerald-mint": "💚 Emerald Matrix",
    "aurora-sunset": "🌅 Aurora Sunset",
    "obsidian-dark": "🖤 Obsidian Dark Indigo",
    "custom-gif": "✨ Кастомная GIF"
};

const obsCover = document.getElementById("cover");
const obsTitle = document.getElementById("title");
const obsArtist = document.getElementById("artist");
const obsProgress = document.getElementById("progress");
const obsCurrent = document.getElementById("current");
const obsDuration = document.getElementById("duration");
const gifBg = document.getElementById("gifBg");
const obsCanvas = document.getElementById("particleCanvas");
const obsCtx = obsCanvas ? obsCanvas.getContext("2d") : null;
const obsPlayer = document.getElementById("player");

const appCover = document.getElementById("coverApp");
const appTitle = document.getElementById("titleApp");
const appArtist = document.getElementById("artistApp");
const appProgress = document.getElementById("progressApp");
const appCurrent = document.getElementById("currentApp");
const appDuration = document.getElementById("durationApp");
const gifBgApp = document.getElementById("gifBgApp");
const appCanvas = document.getElementById("particleCanvasApp");
const appCtx = appCanvas ? appCanvas.getContext("2d") : null;
const appPlayerEl = document.getElementById("appPlayer");

const statusDot = document.getElementById("statusDot");
const statusText = document.getElementById("statusText");
const themeLabel = document.getElementById("currentThemeLabel");

let currentTheme = localStorage.getItem("spotify_studio_theme") || "neon-pink";
let particles = [];
let rafId = null;

function getCanvas() { return isApp ? appCanvas : obsCanvas; }
function getCtx() { return isApp ? appCtx : obsCtx; }
function getPlayerEl() { return isApp ? appPlayerEl : obsPlayer; }

function syncCanvas() {
    const c = getCanvas(), p = getPlayerEl();
    if (!c || !p) return;
    const r = p.getBoundingClientRect();
    c.width = r.width || p.offsetWidth || 530;
    c.height = r.height || p.offsetHeight || 160;
}

function spawnParticles() {
    particles = [];
    const colors = ["#ff007f", "#ff55bb", "#ff99dd", "#ff1493", "#ee00cc", "#cc00ff", "#00f0ff"];
    const c = getCanvas();
    const W = c ? c.width : 530;
    const H = c ? c.height : 160;

    for (let i = 0; i < 45; i++) {
        particles.push({
            x: Math.random() * W,
            y: Math.random() * H,
            r: Math.random() * 2.2 + 0.9,
            vy: Math.random() * 0.7 + 0.25,
            vx: Math.random() * 0.34 - 0.17,
            a: Math.random() * 0.65 + 0.25,
            pulse: Math.random() * 0.018 + 0.004,
            color: colors[Math.floor(Math.random() * colors.length)]
        });
    }
}

function drawParticles() {
    const c = getCanvas(), cx = getCtx();
    if (c && cx) {
        cx.clearRect(0, 0, c.width, c.height);

        if (currentTheme === "custom-gif") {
            const W = c.width, H = c.height;
            for (const p of particles) {
                p.y -= p.vy;
                p.x += Math.sin(p.y * 0.025) * 0.5;
                p.a += Math.sin(Date.now() * p.pulse) * 0.009;

                if (p.y < -8) { p.y = H + 8; p.x = Math.random() * W; }
                if (p.x < -8) p.x = W + 8;
                if (p.x > W + 8) p.x = -8;

                cx.save();
                cx.beginPath();
                cx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
                cx.fillStyle = p.color;
                cx.globalAlpha = Math.min(Math.max(p.a, 0.1), 0.92);
                cx.shadowBlur = p.r * 5;
                cx.shadowColor = p.color;
                cx.fill();
                cx.restore();
            }
        }
    }
    rafId = requestAnimationFrame(drawParticles);
}

window.addEventListener("resize", () => {
    syncCanvas();
    spawnParticles();
});

let currentGifVersion = 0;

async function loadGif(v) {
    const version = v || currentGifVersion || Date.now();
    currentGifVersion = version;
    const cssUrl = `url("/custom_bg.gif?v=${version}")`;

    [gifBg, gifBgApp].forEach(el => {
        if (el) {
            el.style.backgroundImage = "none";
            void el.offsetWidth;
            el.style.backgroundImage = cssUrl;
            el.style.backgroundSize = "cover";
            el.style.backgroundPosition = "center";
        }
    });

    const cardPreview = document.getElementById("cardGifPreview");
    if (cardPreview) {
        cardPreview.style.backgroundImage = "none";
        void cardPreview.offsetWidth;
        cardPreview.style.backgroundImage = cssUrl;
        cardPreview.style.backgroundSize = "cover";
        cardPreview.style.backgroundPosition = "center";
    }
}

window.triggerGifUpload = function() {
    const fileInput = document.getElementById("gifFileInput");
    if (fileInput) fileInput.click();
};

window.handleGifUpload = async function(input) {
    if (!input.files || !input.files.length) return;
    try {
        const res = await fetch("/api/gif/upload", {
            method: "POST",
            body: input.files[0]
        });
        const data = await res.json();
        if (data.success) {
            if (data.gifVersion) currentGifVersion = data.gifVersion;
            window.applyTheme("custom-gif", true);
            await loadGif(data.gifVersion);
        }
    } catch (e) { }
};

function updateActiveThemeCards(name) {
    document.querySelectorAll(".theme-card").forEach(card => {
        const isActive = (card.dataset.theme === name);
        card.classList.toggle("is-active", isActive);
    });

    if (themeLabel) {
        const displayName = THEME_NAMES[name] || name;
        themeLabel.textContent = `Текущая тема: ${displayName}`;
    }
}

window.applyTheme = function(name, notifyServer = true) {
    if (!name) return;
    currentTheme = name;
    localStorage.setItem("spotify_studio_theme", name);
    document.documentElement.setAttribute("data-theme", name);

    updateActiveThemeCards(name);

    if (name === "custom-gif") {
        setTimeout(() => {
            syncCanvas();
            spawnParticles();
        }, 100);
        loadGif();
    }

    if (notifyServer) {
        fetch("/api/theme/set?name=" + encodeURIComponent(name)).catch(() => {});
    }
};

window.setTheme = window.applyTheme;
window.selectTheme = window.applyTheme;
window.selectThemeFromGallery = window.applyTheme;

function formatTime(sec) {
    if (!sec || isNaN(sec)) return "0:00";
    const m = Math.floor(sec / 60);
    const s = Math.floor(sec % 60);
    return `${m}:${String(s).padStart(2, "0")}`;
}

let lastTrackKey = "";

function updatePlayersData(data) {
    const isPlaying = !!(data && data.playing && data.title);

    if (obsPlayer) {
        obsPlayer.classList.toggle("is-hidden", !isPlaying);
    }

    if (statusDot && statusText) {
        if (isPlaying) {
            statusDot.className = "status-indicator online";
            statusText.textContent = `Spotify играет: ${data.artist || ""} — ${data.title || ""}`;
        } else {
            statusDot.className = "status-indicator offline";
            statusText.textContent = "Spotify не активен (включите песню)";
        }
    }

    const titleTxt = isPlaying ? (data.title || "Неизвестный трек") : "Spotify не активен";
    const artistTxt = isPlaying ? (data.artist || "Неизвестный исполнитель") : "Включите трек в Spotify";
    const percentW = isPlaying && data.duration > 0
        ? Math.min(100, Math.max(0, (data.position / data.duration) * 100)) + "%"
        : "0%";
    const curTime = formatTime(data.position);
    const durTime = formatTime(data.duration);

    const trackKey = isPlaying ? `${data.title}|${data.artist}|${data.album}` : "";
    if (trackKey !== lastTrackKey) {
        lastTrackKey = trackKey;
        const coverSrc = isPlaying ? "/api/cover?t=" + Date.now() : "";
        if (obsCover) obsCover.src = coverSrc;
        if (appCover) appCover.src = coverSrc;
    }

    if (obsTitle) obsTitle.textContent = titleTxt;
    if (obsArtist) obsArtist.textContent = artistTxt;
    if (obsProgress) obsProgress.style.width = percentW;
    if (obsCurrent) obsCurrent.textContent = curTime;
    if (obsDuration) obsDuration.textContent = durTime;

    if (appTitle) appTitle.textContent = titleTxt;
    if (appArtist) appArtist.textContent = artistTxt;
    if (appProgress) appProgress.style.width = percentW;
    if (appCurrent) appCurrent.textContent = curTime;
    if (appDuration) appDuration.textContent = durTime;
}

async function pollState() {
    try {
        const res = await fetch("/api/state?t=" + Date.now(), { cache: "no-store" });
        const data = await res.json();

        if (data.theme && data.theme !== currentTheme) {
            window.applyTheme(data.theme, false);
        }

        if (data.gifVersion && data.gifVersion !== currentGifVersion) {
            currentGifVersion = data.gifVersion;
            if (currentTheme === "custom-gif") {
                loadGif(data.gifVersion);
            }
        }

        updatePlayersData(data);
    } catch (e) { }
}

function checkPreviewScreenshots() {
    const themes = ["neon-pink", "deep-purple", "emerald-mint", "aurora-sunset", "obsidian-dark", "custom-gif"];
    themes.forEach(theme => {
        const cardBox = document.querySelector(`.theme-card[data-theme="${theme}"] .card-preview-box`);
        if (!cardBox) return;

        const imgPath = `/previews/${theme}.png?t=` + Date.now();
        const img = new Image();
        img.onload = function() {
            cardBox.innerHTML = `<img src="${imgPath}" alt="${theme} preview" class="preview-screenshot-img">`;
        };
        img.src = `/previews/${theme}.png`;
    });
}

window.applyTheme(currentTheme, false);

setTimeout(() => {
    syncCanvas();
    spawnParticles();
    drawParticles();
    loadGif();
    checkPreviewScreenshots();
}, 50);

setInterval(pollState, 1000);
pollState();