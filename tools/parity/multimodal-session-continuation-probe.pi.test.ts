import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fauxAssistantMessage, type TranscriptContext } from "@earendil-works/pi-ai/compat";
import { afterEach, expect, test, vi } from "vitest";
import { SessionManager } from "../src/core/session-manager.ts";
import { createHarness, type Harness } from "./suite/harness.ts";

vi.mock("../src/utils/image-process.ts", () => ({
	processImage: async (bytes: Uint8Array, mimeType: string) => ({
		ok: true as const,
		data: Buffer.from(bytes).toString("base64"),
		mimeType,
		hints: [],
	}),
}));

const TINY_PNG =
	"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";
const tempRoots: string[] = [];
const harnesses: Harness[] = [];

afterEach(() => {
	while (harnesses.length) harnesses.pop()?.cleanup();
	while (tempRoots.length) {
		const root = tempRoots.pop();
		if (root) rmSync(root, { recursive: true, force: true });
	}
});

function summarizeLastUser(context: TranscriptContext) {
	const user = context.messages.findLast((message) => message.role === "user");
	if (!user || user.role !== "user") throw new Error("Provider context omitted the user message");
	const parts = typeof user.content === "string" ? [{ type: "text", text: user.content }] : user.content;
	return parts.map((part) =>
		part.type === "image"
			? { type: part.type, mimeType: part.mimeType, data: part.data }
			: { type: part.type, text: part.type === "text" ? part.text : "" },
	);
}

test("image survives saved-session reopen and appears once in provider continuation", async () => {
	const root = mkdtempSync(join(tmpdir(), "pi-multimodal-session-"));
	tempRoots.push(root);
	const firstManager = SessionManager.create(root, join(root, "sessions"));
	const first = await createHarness({ sessionManager: firstManager });
	harnesses.push(first);
	let initialProviderParts: ReturnType<typeof summarizeLastUser> = [];
	first.setResponses([
		(context) => {
			initialProviderParts = summarizeLastUser(context);
			return fauxAssistantMessage("received");
		},
	]);
	await first.session.prompt("look", {
		images: [{ type: "image", data: TINY_PNG, mimeType: "image/png" }],
	});
	const sessionFile = firstManager.getSessionFile();
	if (!sessionFile) throw new Error("Session manager did not create a durable session");
	first.cleanup();
	harnesses.pop();

	const reopenedManager = SessionManager.open(sessionFile);
	const reopened = await createHarness({ sessionManager: reopenedManager });
	harnesses.push(reopened);
	let continuationProviderParts: ReturnType<typeof summarizeLastUser> = [];
	reopened.setResponses([
		(context) => {
			const historical = context.messages.find((message) => message.role === "user");
			if (!historical || historical.role !== "user") throw new Error("Historical user turn was lost");
			const parts = typeof historical.content === "string" ? [] : historical.content;
			continuationProviderParts = parts.map((part) =>
				part.type === "image"
					? { type: part.type, mimeType: part.mimeType, data: part.data }
					: { type: part.type, text: part.type === "text" ? part.text : "" },
			);
			return fauxAssistantMessage("continued");
		},
	]);
	await reopened.session.prompt("continue");

	const restoredUser = reopened.sessionManager.buildSessionContext().messages.find((message) => message.role === "user");
	if (!restoredUser || restoredUser.role !== "user") throw new Error("Reopened session has no user message");
	const activeParts = typeof restoredUser.content === "string" ? [] : restoredUser.content;
	writeFileSync(
		process.env.PISHARP_SESSION_OUTPUT!,
		JSON.stringify({ initialProviderParts, continuationProviderParts, restoredUser: activeParts }),
	);
	expect(activeParts.filter((part) => part.type === "image")).toHaveLength(1);
	expect(continuationProviderParts.filter((part) => part.type === "image")).toEqual(
		initialProviderParts.filter((part) => part.type === "image"),
	);
	expect(continuationProviderParts.filter((part) => part.type === "image")).toHaveLength(1);

});
