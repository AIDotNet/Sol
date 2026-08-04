import { expect, test } from "@playwright/test";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { deflateSync, crc32 } from "node:zlib";

/**
 * Canvas smoke tests.
 *
 * These cover the regressions that motivated the follow-up work, so they are deliberately
 * behavioural rather than structural: an uploaded image surviving a reload, and a canvas
 * surviving a full localStorage wipe, are the two failures that lost user data.
 *
 * Requires the .NET API on :5298, like the rest of this suite.
 */

// Serial, unlike the rest of the suite. Every test here drives the same API instance and the
// same database, so running them concurrently adds contention — and identical fingerprint
// signals from one IP, which is exactly the case `MaxCoarseFanout` is designed to refuse — but
// no additional coverage.
test.describe.configure({ mode: "serial" });

/** A tiny valid PNG. Generated rather than committed so the fixture cannot drift. */
function writePng(): string {
  const chunk = (type: string, data: Buffer) => {
    const typed = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(typed) >>> 0);
    return Buffer.concat([length, typed, crc]);
  };

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(8, 0);
  ihdr.writeUInt32BE(8, 4);
  ihdr[8] = 8;
  ihdr[9] = 2;

  const raw = Buffer.concat(
    Array.from({ length: 8 }, () =>
      Buffer.concat([Buffer.from([0]), Buffer.alloc(24, 0x40)]),
    ),
  );

  const png = Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr),
    chunk("IDAT", deflateSync(raw)),
    chunk("IEND", Buffer.alloc(0)),
  ]);

  const path = join(mkdtempSync(join(tmpdir(), "sol-e2e-")), "fixture.png");
  writeFileSync(path, png);
  return path;
}

/** Canvas storage is device-scoped, so every test needs an identity before it can save. */
async function establishDevice(page: import("@playwright/test").Page) {
  await page.goto("/");
  await page.evaluate(async () => {
    const response = await fetch("/api/v1/device/handshake", {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        v: 1,
        stable: { timeZone: "UTC", platform: "e2e" },
        volatile: {},
        clientStoredId: null,
      }),
    });
    localStorage.setItem("sol.device_id", (await response.json()).deviceId);
  });
}

/**
 * Waits for the autosave to reach the server.
 *
 * Keyed on the save indicator rather than a fixed delay: under parallel test load a fixed wait
 * is either flaky or needlessly slow, and this also asserts the indicator works.
 */
async function waitForSaved(page: import("@playwright/test").Page) {
  await expect(page.getByText("已保存")).toBeVisible({ timeout: 20_000 });
}

/** Opens a node's context menu via its header, which no child control overlays. */
async function openNodeMenu(page: import("@playwright/test").Page, index = 0) {
  await page.locator(".react-flow__node header").nth(index).click({ button: "right" });
}

test("the root route renders the canvas directly", async ({ page }) => {
  // No redirect and no id in the path: "/" is the canvas.
  await establishDevice(page);
  await page.goto("/");

  await expect(page.locator(".react-flow")).toBeVisible({ timeout: 15_000 });
  expect(new URL(page.url()).pathname).toBe("/");
});

test("nodes can be added and connected, and survive a reload", async ({ page }) => {
  await establishDevice(page);
  await page.goto("/");
  await expect(page.locator(".react-flow")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("button", { name: "文本节点" }).click();
  await page.getByRole("button", { name: "图片生成" }).click();
  await expect(page.locator(".react-flow__node")).toHaveCount(2);

  await page.locator("textarea").first().fill("a small red house");
  await waitForSaved(page);
  await page.reload();

  await expect(page.locator(".react-flow__node")).toHaveCount(2);
  await expect(page.locator("textarea").first()).toHaveValue("a small red house");
});

test("an uploaded image survives a reload", async ({ page }) => {
  // Regression: the upload used to be an object URL, which was persisted and restored dead,
  // leaving a broken image with no way to re-upload.
  await establishDevice(page);
  await page.goto("/");
  await expect(page.locator(".react-flow")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("button", { name: "图片节点" }).click();
  await page.locator('input[type="file"]').first().setInputFiles(writePng());

  const image = page.locator(".react-flow__node img");
  await expect(image).toHaveAttribute("src", /\/api\/v1\/canvas\/assets\//, { timeout: 15_000 });
  await waitForSaved(page);
  await page.reload();

  await expect(image).toHaveAttribute("src", /\/api\/v1\/canvas\/assets\//, { timeout: 15_000 });

  // naturalWidth of 0 means the image failed to load — the old broken-node symptom.
  const broken = await image.evaluate(
    (element) => (element as HTMLImageElement).complete && (element as HTMLImageElement).naturalWidth === 0,
  );
  expect(broken).toBe(false);
});

test("a canvas survives clearing local storage", async ({ page }) => {
  // The reason the canvas table exists: before it was wired up, clearing site data destroyed
  // every graph the user had made.
  await establishDevice(page);
  await page.goto("/");
  await expect(page.locator(".react-flow")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("button", { name: "文本节点" }).click();
  await page.locator("textarea").first().fill("persisted server-side");
  await waitForSaved(page);

  await page.evaluate(() => localStorage.clear());
  await page.reload();

  await expect(page.locator("textarea").first()).toHaveValue("persisted server-side", {
    timeout: 15_000,
  });
});

test("a node context menu can duplicate and delete", async ({ page }) => {
  await establishDevice(page);
  await page.goto("/");
  await expect(page.locator(".react-flow")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("button", { name: "文本节点" }).click();
  await expect(page.locator(".react-flow__node")).toHaveCount(1);

  await openNodeMenu(page);
  await page.getByText("复制节点").click();
  await expect(page.locator(".react-flow__node")).toHaveCount(2);

  await openNodeMenu(page);
  await page.getByText("删除节点").click();
  await expect(page.locator(".react-flow__node")).toHaveCount(1);
});

test("a chat provider's models are usable once its protocol has a client", async ({ page }) => {
  // Anthropic was configurable but unusable until the text clients landed. This asserts the
  // outcome users see: its models can be enabled, with no "not supported" marker.
  await establishDevice(page);
  await page.goto("/");
  await expect(page.locator(".react-flow")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("button", { name: "设置" }).click();
  await page.getByRole("button", { name: "渠道管理" }).click();

  const addFromEmpty = page.getByRole("button", { name: "添加渠道" }).first();
  if (await addFromEmpty.isVisible().catch(() => false)) {
    await addFromEmpty.click();
  } else {
    await page.locator('button[aria-label="添加渠道"]').click();
  }

  await page.getByText("Anthropic", { exact: true }).first().click();

  // The model list has to render before absence of the marker means anything.
  await expect(page.getByText("claude-sonnet-5")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("尚未支持")).toHaveCount(0);
});
