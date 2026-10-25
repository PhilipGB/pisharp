import { writeFileSync } from "node:fs";
import { beforeEach, expect, test, vi } from "vitest";
import { readClipboardImage } from "../src/utils/clipboard-image.ts";

const mocks = vi.hoisted(() => ({
	command: vi.fn<(command: string, args: string[]) => Promise<Buffer | undefined>>(),
	getImage: vi.fn(),
	getNativeClipboard: vi.fn(),
}));

vi.mock("../src/utils/clipboard-command.ts", () => ({ runClipboardCommand: mocks.command }));
vi.mock("@earendil-works/pi-tui", () => ({ getNativeClipboard: mocks.getNativeClipboard }));

const png = Buffer.from(
	"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==",
	"base64",
);

beforeEach(() => {
	vi.resetAllMocks();
	mocks.getNativeClipboard.mockReturnValue(undefined);
});

test("captures deterministic Linux clipboard acquisition contracts", async () => {
	const scenarios = [
		{
			id: "wayland-prefers-png",
			command: async (name: string, args: string[]) => {
				if (name !== "wl-paste") return undefined;
				return args.includes("--list-types") ? Buffer.from("text/plain\nimage/jpeg\nimage/png\n") : png;
			},
		},
		{
			id: "wayland-empty-does-not-use-stale-x11",
			command: async (name: string, args: string[]) =>
				name === "wl-paste" && args.includes("--list-types") ? Buffer.from("text/plain\n") : png,
		},
		{
			id: "wayland-failure-falls-back-to-x11",
			command: async (name: string, args: string[]) => {
				if (name === "wl-paste") return undefined;
				if (name === "xclip" && args.includes("TARGETS")) return Buffer.from("text/plain\nimage/png\n");
				return name === "xclip" ? png : undefined;
			},
		},
	];
	const results = [];
	for (const scenario of scenarios) {
		mocks.command.mockImplementation(scenario.command);
		const image = await readClipboardImage({ platform: "linux", env: { WAYLAND_DISPLAY: "wayland-test", DISPLAY: ":0" } });
		results.push({
			id: scenario.id,
			mimeType: image?.mimeType ?? null,
			data: image ? Buffer.from(image.bytes).toString("base64") : null,
			calls: mocks.command.mock.calls.map(([name, args]) => [name, args]),
		});
		mocks.command.mockReset();
	}

	expect(results).toHaveLength(3);
	writeFileSync(process.env.PISHARP_CLIPBOARD_OUTPUT!, JSON.stringify(results));
});
