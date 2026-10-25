import { writeFileSync } from "node:fs";
import { test, vi } from "vitest";
import { InteractiveMode } from "../src/modes/interactive/interactive-mode.ts";

const mocks = vi.hoisted(() => ({
	readFilePaths: vi.fn<() => Promise<string[] | null>>(),
	readImage: vi.fn<() => Promise<unknown>>(),
	readText: vi.fn<() => Promise<string | null>>(),
}));

vi.mock("../src/utils/clipboard.ts", () => ({
	readClipboardFilePaths: mocks.readFilePaths,
	readClipboardText: mocks.readText,
	copyToClipboard: vi.fn(),
}));
vi.mock("../src/utils/clipboard-image.ts", () => ({
	readClipboardImage: mocks.readImage,
	extensionForImageMimeType: vi.fn(),
}));

test("captures clipboard file-path precedence over the image fallback", async () => {
	const paths = ["/tmp/screenshot.png", "/tmp/My Photos/photo.png"];
	mocks.readFilePaths.mockResolvedValue(paths);
	mocks.readImage.mockResolvedValue(null);
	mocks.readText.mockResolvedValue(null);
	let text = "Review:";
	const editor = {
		getText: () => text,
		getCursor: () => ({ line: 0, col: text.length }),
		insertTextAtCursor: (value: string) => {
			const cursor = text.length;
			text = text.slice(0, cursor) + value + text.slice(cursor);
		},
	};
	const context = { editor, isBashMode: false, ui: { requestRender: vi.fn() }, showError: vi.fn() };
	const prototype = InteractiveMode.prototype as unknown as {
		handleClipboardPaste(this: typeof context): Promise<void>;
	};
	await prototype.handleClipboardPaste.call(context);

	writeFileSync(process.env.PISHARP_CLIPBOARD_PATH_OUTPUT!, JSON.stringify({
		draft: text,
		imageFallbackReached: mocks.readImage.mock.calls.length > 0,
		textFallbackReached: mocks.readText.mock.calls.length > 0,
	}));
});
