import fs from "node:fs";
import path from "node:path";

const repoRoot = process.cwd();

const EXCLUDED_DIRS = new Set([
    ".git",
    "node_modules"
]);

const EXCLUDED_FILES = new Set([
    "layout.json"
]);

function toWindowsFileTimeTicks(dateMs) {
    const EPOCH_DIFF_MS = 11644473600000;
    return String(Math.floor((dateMs + EPOCH_DIFF_MS) * 10000));
}

function walk(currentDir, results) {
    const entries = fs.readdirSync(currentDir, { withFileTypes: true });

    for (const entry of entries) {
        const absolute = path.join(currentDir, entry.name);
        const relative = path.relative(repoRoot, absolute);

        if (entry.isDirectory()) {
            if (EXCLUDED_DIRS.has(entry.name)) {
                continue;
            }

            walk(absolute, results);
            continue;
        }

        if (!entry.isFile()) {
            continue;
        }

        const normalizedRelative = relative.split(path.sep).join("/");
        if (EXCLUDED_FILES.has(normalizedRelative)) {
            continue;
        }

        const stat = fs.statSync(absolute);

        results.push({
            path: normalizedRelative,
            size: stat.size,
            date: toWindowsFileTimeTicks(stat.mtimeMs)
        });
    }
}

const files = [];
walk(repoRoot, files);
files.sort((a, b) => a.path.localeCompare(b.path));

const totalSize = files.reduce((sum, item) => sum + item.size, 0);

const layout = {
    content: files
};

fs.writeFileSync(path.join(repoRoot, "layout.json"), JSON.stringify(layout, null, 2) + "\n");

const manifestPath = path.join(repoRoot, "manifest.json");
if (fs.existsSync(manifestPath)) {
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    manifest.total_package_size = String(totalSize).padStart(20, "0");

    if (!manifest.release_notes) {
        manifest.release_notes = { neutral: { LastUpdate: "", OlderHistory: [] } };
    }

    if (!manifest.release_notes.neutral) {
        manifest.release_notes.neutral = { LastUpdate: "", OlderHistory: [] };
    }

    manifest.release_notes.neutral.LastUpdate = new Date().toISOString().slice(0, 10);

    fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");
}

console.log(`Generated layout.json with ${files.length} file(s); total size ${totalSize} bytes.`);
