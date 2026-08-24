import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import KuroshiroModule from "kuroshiro";
import KuromojiAnalyzerModule from "kuroshiro-analyzer-kuromoji";

const Kuroshiro = KuroshiroModule.default ? KuroshiroModule.default : KuroshiroModule;
const KuromojiAnalyzer = KuromojiAnalyzerModule.default ? KuromojiAnalyzerModule.default : KuromojiAnalyzerModule;

function resolveDictionaryPath() {
    const scriptDir = path.dirname(fileURLToPath(import.meta.url));
    const candidates = [
        path.join(scriptDir, "node_modules", "kuromoji", "dict"),
        path.join(scriptDir, "dict", "kuromoji.js", "dict")
    ];

    return candidates.find((candidate) =>
        fs.existsSync(path.join(candidate, "tid_pos.dat.gz"))
    );
}

async function run() {
    const args = process.argv.slice(2);
    const inputParts = [];
    let mode = "spaced";
    let to = "romaji";
    let jsonLines = false;

    for (let i = 0; i < args.length; i++) {
        if (args[i] === "--mode" && args[i + 1]) {
            mode = args[i + 1];
            i++;
        } else if (args[i] === "--to" && args[i + 1]) {
            to = args[i + 1];
            i++;
        } else if (args[i] === "--json-lines") {
            jsonLines = true;
        } else {
            inputParts.push(args[i]);
        }
    }

    const inputText = inputParts.join(" ").trim();
    if (!inputText) {
        console.error("Input text is required.");
        process.exit(1);
    }

    const dictPath = resolveDictionaryPath();
    if (!dictPath) {
        console.error("Kuromoji dictionary files were not found.");
        process.exit(1);
    }

    const kuroshiro = new Kuroshiro();
    await kuroshiro.init(new KuromojiAnalyzer({ dictPath }));

    if (jsonLines) {
        const lines = JSON.parse(inputText);
        if (!Array.isArray(lines)) {
            console.error("JSON lines input must be an array.");
            process.exit(1);
        }

        const results = [];
        for (const line of lines) {
            results.push(await kuroshiro.convert(line ?? "", { mode, to }));
        }

        console.log(JSON.stringify(results));
        return;
    }

    const result = await kuroshiro.convert(inputText, { mode, to });
    console.log(result);
}

run().catch((error) => {
    console.error("Conversion failed:", error);
    process.exit(1);
});
