#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commctrl.h>
#include <shlobj.h>
#include <process.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#include "cdr.h"

enum { OPEN = 100, SEARCH, GRID, LOADED = WM_APP + 1 };
enum { MAX_LINE = 262144 };

typedef struct {
    wchar_t *cell[COLUMN_COUNT];
    long long time[3];
    int has_time[3];
} DisplayRow;

typedef struct {
    wchar_t folder[MAX_PATH];
    DisplayRow *rows;
    int count, capacity, files, skipped, errors, limited;
} Results;

static const wchar_t *headings[] = {
    L"Date / setup time", L"Calling", L"Called", L"Duration (s)", L"Codec",
    L"Disconnected by", L"Cause code (raw)", L"Reason", L"Forwarded", L"Forwarded to",
    L"Remote signaling IP", L"Remote SIP port", L"Remote UDP port", L"Remote media IP",
    L"Remote media port", L"Local signaling IP", L"Local SIP port", L"Local media IP",
    L"Local media port", L"Connect time", L"Disconnect time",
    L"Call origin", L"Call ID", L"Conference ID", L"Source file : line"
};
static const int widths[] = {245,120,120,99,115,174,122,225,122,125,170,125,125,170,125,170,125,170,125,245,245,100,115,235,235};

static HWND window, open_button, search_label, search_box, grid, status;
static Results *results;
static int *visible;
static int visible_count, sort_column = -1, descending, loading;

static void free_results(Results *data)
{
    if (!data) return;
    for (int row = 0; row < data->count; ++row)
        for (int col = 0; col < COLUMN_COUNT; ++col) free(data->rows[row].cell[col]);
    free(data->rows);
    free(data);
}

static wchar_t *to_wide(const char *value)
{
    if (!*value) return NULL;
    UINT encoding = CP_UTF8;
    DWORD flags = MB_ERR_INVALID_CHARS;
    int size = MultiByteToWideChar(encoding, flags, value, -1, NULL, 0);
    if (!size) {
        encoding = CP_ACP;
        flags = 0;
        size = MultiByteToWideChar(encoding, flags, value, -1, NULL, 0);
    }
    if (!size) return NULL;
    wchar_t *buffer = malloc((size_t)size * sizeof(*buffer));
    if (buffer) MultiByteToWideChar(encoding, flags, value, -1, buffer, size);
    return buffer;
}

static int append(Results *data, const Record *record, const wchar_t *filename, int line)
{
    if (data->count == 250000) return 0;
    if (data->count == data->capacity) {
        int capacity = data->capacity ? 2 * data->capacity : 1024;
        DisplayRow *rows = realloc(data->rows, (size_t)capacity * sizeof(*rows));
        if (!rows) return 0;
        data->rows = rows;
        data->capacity = capacity;
    }
    DisplayRow row = {0};
    const int time_columns[] = {SETUP, CONNECT, END};
    for (int i = 0; i < 3; ++i) {
        char zone[16];
        row.has_time[i] = cdr_timestamp(record->cell[time_columns[i]], &row.time[i], zone);
    }
    for (int col = 0; col < SOURCE; ++col) {
        row.cell[col] = to_wide(record->cell[col]);
        if (*record->cell[col] && !row.cell[col]) goto failed;
    }
    wchar_t source[MAX_PATH + 32];
    swprintf(source, sizeof(source) / sizeof(*source), L"%ls : %d", filename, line);
    row.cell[SOURCE] = _wcsdup(source);
    if (!row.cell[SOURCE]) goto failed;
    data->rows[data->count++] = row;
    return 1;
failed:
    for (int col = 0; col < COLUMN_COUNT; ++col) free(row.cell[col]);
    return 0;
}

static int is_candidate(const wchar_t *name)
{
    const wchar_t *ext = wcsrchr(name, L'.');
    if (!ext || !_wcsicmp(ext, L".csv") || !_wcsicmp(ext, L".cdr") ||
        !_wcsicmp(ext, L".log") || !_wcsicmp(ext, L".txt")) return 1;
    return ext[1] && wcsspn(ext + 1, L"0123456789") == wcslen(ext + 1);
}

static void scan_file(Results *data, const wchar_t *filename)
{
    wchar_t path[2 * MAX_PATH];
    swprintf(path, sizeof(path) / sizeof(*path), L"%ls\\%ls", data->folder, filename);
    FILE *file = _wfopen(path, L"rb");
    if (!file) { ++data->errors; return; }
    ++data->files;
    char *buffer = malloc(MAX_LINE);
    if (!buffer) { data->limited = 1; fclose(file); return; }
    Schema header, *schema = NULL;
    int line = 0;
    while (fgets(buffer, MAX_LINE, file)) {
        ++line;
        size_t length = strlen(buffer);
        if (length == MAX_LINE - 1 && buffer[length - 1] != '\n') {
            int ch;
            while ((ch = fgetc(file)) != EOF && ch != '\n') {}
            ++data->skipped;
            continue;
        }
        buffer[strcspn(buffer, "\r\n")] = '\0';
        char *start = buffer;
        if (line == 1 && length >= 3 && !memcmp(start, "\xef\xbb\xbf", 3)) start += 3;
        start += strspn(start, " \t");
        if (!*start) continue;
        char *field[MAX_FIELDS];
        int count = split_csv(start, field);
        if (count > 0 && read_header(field, count, &header)) { schema = &header; continue; }
        Record record;
        if (count < 0 || !read_record(field, count, schema, &record)) {
            ++data->skipped;
            continue;
        }
        if (!append(data, &record, filename, line)) { data->limited = 1; break; }
    }
    if (ferror(file)) ++data->errors;
    free(buffer);
    fclose(file);
}

static unsigned __stdcall scan_folder(void *context)
{
    Results *data = context;
    wchar_t search_path[MAX_PATH + 3];
    swprintf(search_path, sizeof(search_path) / sizeof(*search_path), L"%ls\\*", data->folder);
    WIN32_FIND_DATAW entry;
    HANDLE scan = FindFirstFileW(search_path, &entry);
    if (scan != INVALID_HANDLE_VALUE) {
        do {
            if (!(entry.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) &&
                is_candidate(entry.cFileName)) scan_file(data, entry.cFileName);
            if (data->limited) break;
        } while (FindNextFileW(scan, &entry));
        if (!data->limited && GetLastError() != ERROR_NO_MORE_FILES) ++data->errors;
        FindClose(scan);
    } else if (GetLastError() != ERROR_FILE_NOT_FOUND) {
        ++data->errors;
    }
    if (!PostMessageW(window, LOADED, 0, (LPARAM)data)) free_results(data);
    return 0;
}

static void choose_folder(void)
{
    if (loading) return;
    BROWSEINFOW options = {0};
    options.hwndOwner = window;
    options.lpszTitle = L"Select a folder containing Cisco CUBE file accounting records";
    options.ulFlags = BIF_RETURNONLYFSDIRS | BIF_USENEWUI | BIF_NONEWFOLDERBUTTON;
    PIDLIST_ABSOLUTE selection = SHBrowseForFolderW(&options);
    if (!selection) return;
    Results *next = calloc(1, sizeof(*next));
    int valid = next && SHGetPathFromIDListW(selection, next->folder);
    CoTaskMemFree(selection);
    if (!valid) {
        free(next);
        MessageBoxW(window, L"Could not open that folder.", L"CUBE CDR Reader", MB_ICONERROR);
        return;
    }
    loading = 1;
    EnableWindow(open_button, FALSE);
    SetWindowTextW(status, L"Reading accounting records...");
    uintptr_t worker = _beginthreadex(NULL, 0, scan_folder, next, 0, NULL);
    if (worker) {
        CloseHandle((HANDLE)worker);
    } else {
        free(next);
        loading = 0;
        EnableWindow(open_button, TRUE);
        SetWindowTextW(status, L"Could not start reading the folder.");
    }
}

static const wchar_t *cell(int row, int column)
{
    const wchar_t *value = results->rows[row].cell[column];
    return value ? value : L"";
}

static int compare_rows(const void *first, const void *second)
{
    int a = *(const int *)first, b = *(const int *)second;
    const wchar_t *left = cell(a, sort_column), *right = cell(b, sort_column);
    int order = CompareStringOrdinal(left, -1, right, -1, TRUE) - CSTR_EQUAL;
    int time_index = sort_column == SETUP ? 0 : sort_column == CONNECT ? 1 : sort_column == END ? 2 : -1;
    if (time_index >= 0) {
        const DisplayRow *x = &results->rows[a], *y = &results->rows[b];
        if (x->has_time[time_index] != y->has_time[time_index])
            order = y->has_time[time_index] - x->has_time[time_index];
        else if (x->has_time[time_index])
            order = (x->time[time_index] > y->time[time_index]) -
                    (x->time[time_index] < y->time[time_index]);
    }
    if (sort_column == DURATION) {
        wchar_t *left_end, *right_end;
        double x = wcstod(left, &left_end), y = wcstod(right, &right_end);
        int numeric_x = left_end != left && !*left_end, numeric_y = right_end != right && !*right_end;
        if (numeric_x != numeric_y) order = numeric_y - numeric_x;
        else if (numeric_x) order = (x > y) - (x < y);
    }
    return order ? (descending ? -order : order) : (a > b) - (a < b);
}

static void filter_rows(void)
{
    if (!results || loading) return;
    wchar_t query[256];
    GetWindowTextW(search_box, query, 256);
    visible_count = 0;
    if (results->count && !visible) {
        SetWindowTextW(status, L"Insufficient memory to display the records.");
        return;
    }
    int length = (int)wcslen(query);
    for (int row = 0; row < results->count; ++row) {
        int matches = !length;
        for (int col = 0; !matches && col < COLUMN_COUNT; ++col) {
            const wchar_t *value = cell(row, col);
            int remaining = (int)wcslen(value);
            for (; remaining >= length && !matches; --remaining, ++value)
                matches = CompareStringOrdinal(value, length, query, length, TRUE) == CSTR_EQUAL;
        }
        if (matches) visible[visible_count++] = row;
    }
    if (sort_column >= 0 && visible_count > 1)
        qsort(visible, (size_t)visible_count, sizeof(*visible), compare_rows);
    ListView_SetItemCount(grid, visible_count);
    InvalidateRect(grid, NULL, TRUE);
    wchar_t summary[460];
    swprintf(summary, sizeof(summary) / sizeof(*summary),
             L"%d of %d records   |   %d files read   |   %d rows skipped   |   %d file errors%ls",
             visible_count, results->count, results->files, results->skipped, results->errors,
             results->limited ? L"   |   Import limit reached" : L"");
    SetWindowTextW(status, summary);
}

static HWND control(const wchar_t *type, const wchar_t *label, DWORD flags, DWORD extra, int id)
{
    HWND child = CreateWindowExW(extra, type, label, WS_CHILD | WS_VISIBLE | flags,
                                0, 0, 0, 0, window, (HMENU)(INT_PTR)id, GetModuleHandleW(NULL), NULL);
    SendMessageW(child, WM_SETFONT, (WPARAM)GetStockObject(DEFAULT_GUI_FONT), TRUE);
    return child;
}

static LRESULT CALLBACK procedure(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam)
{
    switch (message) {
    case WM_CREATE:
        window = hwnd;
        open_button = control(L"BUTTON", L"Open Folder...", WS_TABSTOP, 0, OPEN);
        search_label = control(L"STATIC", L"Search", SS_CENTERIMAGE, 0, 0);
        search_box = control(L"EDIT", L"", WS_TABSTOP | ES_AUTOHSCROLL, WS_EX_CLIENTEDGE, SEARCH);
        SendMessageW(search_box, EM_SETLIMITTEXT, 255, 0);
        grid = control(WC_LISTVIEWW, L"", WS_TABSTOP | LVS_REPORT | LVS_OWNERDATA | LVS_SHOWSELALWAYS,
                       WS_EX_CLIENTEDGE, GRID);
        ListView_SetExtendedListViewStyle(grid, LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER | LVS_EX_GRIDLINES);
        for (int col = 0; col < COLUMN_COUNT; ++col) {
            LVCOLUMNW column = {0};
            column.mask = LVCF_WIDTH | LVCF_TEXT;
            column.cx = widths[col];
            column.pszText = (wchar_t *)headings[col];
            ListView_InsertColumn(grid, col, &column);
        }
        status = control(L"STATIC", L"Open a folder to read CUBE call legs.", SS_CENTERIMAGE, 0, 0);
        return 0;
    case WM_SIZE: {
        int width = LOWORD(lparam), height = HIWORD(lparam);
        if (width < 420 || height < 155) return 0;
        MoveWindow(open_button, 12, 12, 122, 29, TRUE);
        MoveWindow(search_label, 150, 12, 46, 29, TRUE);
        MoveWindow(search_box, 204, 14, width - 216, 25, TRUE);
        MoveWindow(grid, 12, 53, width - 24, height - 88, TRUE);
        MoveWindow(status, 12, height - 30, width - 24, 21, TRUE);
        return 0;
    }
    case WM_GETMINMAXINFO: {
        MINMAXINFO *limits = (MINMAXINFO *)lparam;
        limits->ptMinTrackSize.x = 670;
        limits->ptMinTrackSize.y = 240;
        return 0;
    }
    case WM_COMMAND:
        if (LOWORD(wparam) == OPEN && HIWORD(wparam) == BN_CLICKED) choose_folder();
        if (LOWORD(wparam) == SEARCH && HIWORD(wparam) == EN_CHANGE) filter_rows();
        return 0;
    case WM_NOTIFY: {
        NMHDR *event = (NMHDR *)lparam;
        if (event->idFrom != GRID) break;
        if (event->code == LVN_GETDISPINFOW) {
            NMLVDISPINFOW *info = (NMLVDISPINFOW *)lparam;
            int row = info->item.iItem, col = info->item.iSubItem;
            if ((info->item.mask & LVIF_TEXT) && row >= 0 && row < visible_count &&
                col >= 0 && col < COLUMN_COUNT)
                info->item.pszText = (wchar_t *)cell(visible[row], col);
        }
        if (event->code == LVN_COLUMNCLICK) {
            NMLISTVIEW *item = (NMLISTVIEW *)lparam;
            if (item->iSubItem < 0 || item->iSubItem >= COLUMN_COUNT) return 0;
            descending = sort_column == item->iSubItem && !descending;
            sort_column = item->iSubItem;
            filter_rows();
        }
        return 0;
    }
    case LOADED:
        ListView_SetItemCount(grid, 0);
        visible_count = 0;
        free(visible);
        free_results(results);
        results = (Results *)lparam;
        visible = results->count ? malloc((size_t)results->count * sizeof(*visible)) : NULL;
        loading = 0;
        EnableWindow(open_button, TRUE);
        filter_rows();
        return 0;
    case WM_CLOSE:
        if (loading) return 0;
        DestroyWindow(hwnd);
        return 0;
    case WM_DESTROY:
        free(visible);
        free_results(results);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int show)
{
    (void)previous;
    (void)arguments;
    if (FAILED(CoInitializeEx(NULL, COINIT_APARTMENTTHREADED))) return 1;
    INITCOMMONCONTROLSEX common = {sizeof(common), ICC_LISTVIEW_CLASSES};
    InitCommonControlsEx(&common);
    WNDCLASSW definition = {0};
    definition.lpfnWndProc = procedure;
    definition.hInstance = instance;
    definition.hCursor = LoadCursorW(NULL, IDC_ARROW);
    definition.hIcon = LoadIconW(NULL, IDI_APPLICATION);
    definition.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1);
    definition.lpszClassName = L"CubeCdrReader";
    RegisterClassW(&definition);
    HWND app = CreateWindowW(definition.lpszClassName, L"CUBE CDR Reader", WS_OVERLAPPEDWINDOW,
                             CW_USEDEFAULT, CW_USEDEFAULT, 1120, 660, NULL, NULL, instance, NULL);
    if (app) {
        ShowWindow(app, show);
        MSG event;
        while (GetMessageW(&event, NULL, 0, 0) > 0) {
            if (!IsDialogMessageW(app, &event)) {
                TranslateMessage(&event);
                DispatchMessageW(&event);
            }
        }
    }
    CoUninitialize();
    return app ? 0 : 1;
}
