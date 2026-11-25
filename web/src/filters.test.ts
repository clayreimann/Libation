import { describe, expect, it } from "vitest";
import { audibleUrl, withHideEpisodes } from "./filters";

describe("withHideEpisodes", () => {
  it("passes the query through when off", () => {
    expect(withHideEpisodes(" a ", false)).toBe("a");
  });
  it("negates episodes alone or around the query", () => {
    expect(withHideEpisodes("", true)).toBe("-Episode");
    expect(withHideEpisodes("title:x OR y", true)).toBe("(title:x OR y) AND -Episode");
  });
});

describe("audibleUrl", () => {
  it("maps locale names to store domains", () => {
    expect(audibleUrl("uk", "B0")).toBe("https://www.audible.co.uk/pd/B0");
    expect(audibleUrl("germany", "B0")).toBe("https://www.audible.de/pd/B0");
    expect(audibleUrl("mars", "B0")).toBe("https://www.audible.com/pd/B0");
  });
});
