import { chromium } from "@playwright/test";
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1000, height: 640 }, colorScheme: "dark" });
await page.goto("http://localhost:3000/", { waitUntil: "networkidle" });
await page.evaluate(() => localStorage.setItem("theme", "dark"));
await page.reload({ waitUntil: "networkidle" });
await page.waitForTimeout(3500);
await page.getByRole("button", { name: "文本节点" }).click();
await page.waitForTimeout(800);
await page.screenshot({ path: "/tmp/shot-dark-before.png" });

const styles = await page.evaluate(() => {
  const pick = (sel) => {
    const el = document.querySelector(sel);
    if (!el) return null;
    const s = getComputedStyle(el);
    return { bg: s.backgroundColor, color: s.color };
  };
  return {
    html: document.documentElement.className,
    controls: pick(".react-flow__controls"),
    controlBtn: pick(".react-flow__controls button"),
    minimap: pick(".react-flow__minimap"),
    flow: pick(".react-flow"),
  };
});
console.log(JSON.stringify(styles, null, 1));
await browser.close();
