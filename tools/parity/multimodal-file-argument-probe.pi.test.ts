import { writeFileSync } from "node:fs";
import { test } from "vitest";
import { buildInitialMessage } from "../src/cli/initial-message.ts";
import type { Args } from "../src/cli/args.ts";
import { processFileArguments } from "../../coding-agent/src/cli/file-processor.ts";
import { resizeImage } from "../../coding-agent/src/utils/image-resize.ts";

test("captures pinned Pi positional-file processing", async () => {
	const scenarios = JSON.parse(process.env.PISHARP_FILE_SCENARIOS!) as Array<{ id: string; prompt: string; files: string[] }>;
	const root = process.env.PISHARP_FILE_ROOT!;
	process.chdir(root);
	const capture = async () => {
		const results = [];
		for (const scenario of scenarios) {
			const processed = await processFileArguments(scenario.files, { autoResizeImages: false });
			const parsed = { messages: [scenario.prompt], fileArgs: [], unknownFlags: new Map(), diagnostics: [] } as Args;
			const initial = buildInitialMessage({ parsed, fileText: processed.text, fileImages: processed.images });
			const images = [];
			for (const image of initial.initialImages ?? []) {
				if (scenario.id === "supported-image-format-passthrough") {
					images.push({ mimeType: image.mimeType, data: image.data, width: null, height: null });
					continue;
				}
				const decoded = await resizeImage(Buffer.from(image.data, "base64"), image.mimeType, {
					maxWidth: 10000,
					maxHeight: 10000,
					maxBytes: 100000000,
				});
				if (!decoded) throw new Error(`Could not inspect image in ${scenario.id}`);
				images.push({ mimeType: image.mimeType, data: image.data, width: decoded.width, height: decoded.height });
			}
			results.push({ id: scenario.id, text: initial.initialMessage ?? "", images });
		}
		return results;
	};
	const first = await capture();
	const second = await capture();
	writeFileSync(process.env.PISHARP_FILE_OUTPUT!, JSON.stringify({ first, second, cwd: root }));
});
