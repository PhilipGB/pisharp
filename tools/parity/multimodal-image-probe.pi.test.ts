import { writeFileSync } from "node:fs";
import { test } from "vitest";
import { processImage } from "../../coding-agent/src/utils/image-process.ts";
import { resizeImage } from "../../coding-agent/src/utils/image-resize.ts";

test("captures pinned Pi prompt-image processing", async () => {
	const scenarios = JSON.parse(process.env.PISHARP_IMAGE_SCENARIOS!) as Array<{
		id: string;
		data: string;
		mimeType: string;
		resize?: { maxWidth?: number; maxHeight?: number; maxBytes?: number; jpegQuality?: number };
		autoResizeImages?: boolean;
	}>;
	const capture = async () => {
		const results = [];
		for (const scenario of scenarios) {
			const input = Buffer.from(scenario.data, "base64");
			const result = await processImage(input, scenario.mimeType, {
				autoResizeImages: scenario.autoResizeImages,
				resizeOptions: scenario.resize,
			});
			if (!result.ok) {
				results.push({ id: scenario.id, ok: false, message: result.message });
				continue;
			}
			if (["gif-pass-through", "webp-pass-through"].includes(scenario.id)) {
				results.push({ id: scenario.id, ok: true, mimeType: result.mimeType, data: result.data,
					width: null, height: null, hints: result.hints });
				continue;
			}
			const decoded = await resizeImage(Buffer.from(result.data, "base64"), result.mimeType, {
				maxWidth: 10000,
				maxHeight: 10000,
				maxBytes: 100000000,
			});
			if (!decoded) throw new Error(`Could not inspect output for ${scenario.id}`);
			results.push({
				id: scenario.id,
				ok: true,
				mimeType: result.mimeType,
				data: result.data,
				width: decoded.width,
				height: decoded.height,
				hints: result.hints,
			});
		}
		return results;
	};
	const first = await capture();
	const second = await capture();
	writeFileSync(process.env.PISHARP_IMAGE_OUTPUT!, JSON.stringify({ first, second }));
});
