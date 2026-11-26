# Changelog

## 1.1.0

- **Export to CSV:** save the records currently shown, after filtering and sorting.
- **Copy rows:** select rows and press Ctrl+C (or right-click) to paste them into a spreadsheet, with headings. Ctrl+A selects every row shown.
- Drag a file or folder onto the window to open it.
- The version number shows in the title bar.
- An unexpected error while reading files shows a message instead of closing the program.
- Open a file or folder given on the command line, or dropped onto the program.
- Added a made-up sample file, `samples/sample-cdrs.csv`.
- Releases now include the original portable C version and SHA-256 checksums. The download link always points to the latest release.
- For developers: the parser is now a separate library with automated tests, and every pull request runs the tests and builds the app.

## 1.0.0

- Windows app with twelve filters, replacing the original C reader.
