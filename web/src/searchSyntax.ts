/** Mirrors SearchSyntaxDialog; names from SearchEngine.FieldIndexRules. */
export interface FieldGroup {
  title: string;
  usage: string;
  fields: string[];
}

export const fieldGroups: FieldGroup[] = [
  {
    title: "Text",
    usage: 'title:oz\ntitle:"wizard of oz"',
    fields: [
      "Title", "Author", "Authors", "AuthorNames", "Narrator", "Narrators", "NarratorNames", "Publisher", "Series", "SeriesNames",
      "SeriesId", "Category", "Categories", "CategoryId", "Tags", "Locale", "Region", "Account", "Email", "Codec", "DownloadedCodec",
    ],
  },
  {
    title: "Numbers and dates",
    usage: "length:[1 TO 100]\nlength:60\ndatepublished:[20200101 TO 20231231]",
    fields: [
      "Length", "Minutes", "Hours", "Rating", "ProductRating", "UserRating", "MyRating", "DatePublished", "DateAdded",
      "LastDownloaded", "Bitrate", "SampleRate",
    ],
  },
  {
    title: "Yes/no",
    usage: "-IsRated",
    fields: [
      "IsLiberated", "LiberatedError", "HasPDF", "IsRated", "IsAuthorNarrated", "Abridged", "Spatial", "Podcast", "Episode",
      "Absent", "IsInSeries", "HasSubtitle", "TitleHasColon", "Finished", "AudiblePlus",
    ],
  },
  { title: "ID", usage: "id:B015D78L0U", fields: ["Id", "ASIN", "ProductId"] },
];

export const syntaxNotes = [
  "Plain words search title, authors, narrators and ID.",
  "Combine with AND, OR, NOT or a leading minus: potter -harry",
  "Search a tag with brackets: [auto_bio] also matches [bio]",
  "A word that is also a field name means the field; use title:absent for the word.",
];
