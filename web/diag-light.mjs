import { chromium } from "@playwright/test";
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1000, height: 640 } });
await page.goto("http://localhost:3000/", { waitUntil: "networkidle" });
await page.evaluate(() => localStorage.setItem("theme", "light"));
await page.reload({ waitUntil: "networkidle" });
await page.waitForTimeout(3500);
await page.getByRole("button", { name: "文本节点" }).click();
await page.waitForTimeout(700);
const s = await page.evaluate(() => {
  const b = document.querySelector(".react-flow__controls button");
  const c = getComputedStyle(b);
  return { bg: c.backgroundColor, color: c.color };
});
console.log("LIGHT controlBtn", JSON.stringify(s));
await page.screenshot({ path: "/tmp/shot-light.png" });
await browser.close();
