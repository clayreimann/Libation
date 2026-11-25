import { describe, expect, it } from "vitest";
import { descriptionText } from "./description";
import { stars } from "./format";
import { seriesQuery } from "./filters";

describe("descriptionText", () => {
  it("returns empty for nothing", () => {
    expect(descriptionText(null)).toBe("");
    expect(descriptionText(undefined)).toBe("");
  });

  it("keeps paragraph breaks and strips tags", () => {
    expect(descriptionText("<p>One <b>bold</b></p><p>Two<br/>three</p>")).toBe("One bold\n\nTwo\nthree");
  });

  it("drops script and style bodies entirely", () => {
    expect(descriptionText("a<script>alert(1)</script>b<style>x{}</style>c")).toBe("abc");
  });

  it("decodes entities once, without re-creating markup", () => {
    expect(descriptionText("Tom &amp; Jerry &lt;b&gt; &#39;hi&#39; &#x41;")).toBe("Tom & Jerry <b> 'hi' A");
    expect(descriptionText("&amp;lt;")).toBe("&lt;");
  });

  it("renders list items as bullets", () => {
    expect(descriptionText("<ul><li>a</li><li>b</li></ul>")).toBe("• a\n\n• b");
  });

  it("ignores out-of-range numeric entities", () => {
    expect(descriptionText("&#99999999;")).toBe("&#99999999;");
  });
});

describe("helpers", () => {
  it("stars round and clamp", () => {
    expect(stars(0)).toBe("");
    expect(stars(4.4)).toBe("★★★★☆");
    expect(stars(9)).toBe("★★★★★");
  });

  it("builds series queries", () => {
    expect(seriesQuery("B0ABC")).toBe("SeriesId:B0ABC");
  });
});
