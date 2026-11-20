#include "cdr.h"

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int split_csv(char *line, char *field[MAX_FIELDS])
{
    char *read = line, *write = line;
    int count = 0;
    for (;;) {
        if (count == MAX_FIELDS) return -1;
        while (*read == ' ' || *read == '\t') ++read;
        field[count++] = write;
        if (*read == '"') {
            ++read;
            for (;;) {
                if (!*read) return -1;
                if (*read == '"') {
                    if (*++read != '"') break;
                }
                *write++ = *read++;
            }
            while (*read == ' ' || *read == '\t') ++read;
            if (*read && *read != ',') return -1;
        } else {
            char *start = write;
            while (*read && *read != ',') *write++ = *read++;
            while (write > start && isspace((unsigned char)write[-1])) --write;
        }
        int more = *read == ',';
        if (more) ++read;
        *write++ = '\0';
        if (!more) return count;
    }
}

static int same_key(const char *value, const char *key)
{
    while (*value && *key) {
        if (!isalnum((unsigned char)*value)) { ++value; continue; }
        if (!isalnum((unsigned char)*key)) { ++key; continue; }
        if (tolower((unsigned char)*value++) != tolower((unsigned char)*key++)) return 0;
    }
    return !*value && !*key;
}

static int locate(char *const *field, int count, const char *a, const char *b, const char *c)
{
    const char *keys[] = {a, b, c};
    for (int k = 0; k < 3 && keys[k]; ++k)
        for (int i = 0; i < count; ++i)
            if (same_key(field[i], keys[k])) return i;
    return -1;
}

int read_header(char *const *field, int count, Schema *schema)
{
#define COL(a,b,c) locate(field, count, a, b, c)
    Schema candidate = {
        COL("h323-setup-time","setup-time","start-time"),
        COL("clid","calling-station-id","calling-number"),
        COL("dnis","called-station-id","called-number"),
        COL("duration-seconds","acct-session-time","duration"),
        COL("codec-type-rate","codec","negotiated-codec"),
        COL("release-source","disconnect-party","disconnect-initiator"),
        COL("h323-disconnect-cause","disconnect-cause","reason-code"),
        COL("disconnect-text","disconnect-reason","reason-text"),
        COL("h323-connect-time","connect-time",NULL),
        COL("h323-disconnect-time","disconnect-time","end-time"),
        COL("h323-call-origin","call-origin","leg"),
        COL("call-id","sip-call-id",NULL),
        COL("h323-conf-id","conference-id",NULL)
    };
#undef COL
    if (candidate.setup < 0 && candidate.end < 0) return 0;
    if (candidate.calling < 0 && candidate.called < 0 && candidate.call_id < 0) return 0;
    *schema = candidate;
    return 1;
}

static const char *at(char *const *field, int count, int index)
{
    return index >= 0 && index < count ? field[index] : "";
}

static int digits(const char *text)
{
    return *text && text[strspn(text, "0123456789")] == '\0';
}

static int clock_like(const char *text)
{
    if (*text == '*') ++text;
    return isdigit((unsigned char)*text) && strchr(text, ':') != NULL;
}

static int cause_like(const char *text)
{
    if (!*text) return 1;
    if (text[0] == '0' && (text[1] == 'x' || text[1] == 'X')) text += 2;
    return *text && text[strspn(text, "0123456789abcdefABCDEF")] == '\0';
}

static int positional(char *const *field, int count, Schema *schema)
{
    if (count < 21 || !digits(field[0]) || !digits(field[1]) ||
        strcmp(field[2], "1") || strcmp(field[3], "2")) return 0;

    /* Cisco's table and published examples include three variants before the cause. */
    int cause = 0;
    for (int i = 9; i <= 12; ++i) {
        if (clock_like(field[i - 1]) && cause_like(field[i]) &&
            (!*field[i + 1] || !cause_like(field[i + 1])) &&
            (*field[i - 2] == '\0' || clock_like(field[i - 2]))) {
            cause = i;
            break;
        }
    }
    if (!cause || count <= cause + 11) return 0;
    int setup = 7;
    if (cause == 9 || clock_like(field[6])) setup = 6;
    if (!clock_like(field[setup]) && *field[setup]) return 0;
    int codec = cause + 23;
    if (count > cause + 24 && (!*at(field, count, codec) || digits(field[codec])) &&
        isalpha((unsigned char)field[cause + 24][0]))
        codec = cause + 24;
    if (digits(at(field, count, codec))) codec = -1;
    *schema = (Schema){
        setup, cause + 10, cause + 11, -1, codec, -1, cause, cause + 1,
        cause - 2, cause - 1, cause + 2, 1, 4
    };
    return 1;
}

static int leap(int year)
{
    return year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
}

int cdr_timestamp(const char *text, long long *millis, char zone[16])
{
    int year, month, day, hour, minute, second, end = 0;
    char weekday[16], month_name[16];
    static const char *months[] = {"Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"};
    static const int days_in_month[] = {31,28,31,30,31,30,31,31,30,31,30,31};
    if (*text == '*') ++text;
    int iso = sscanf(text, "%4d-%2d-%2d%*[T ]%2d:%2d:%2d%n",
                     &year, &month, &day, &hour, &minute, &second, &end) == 6;
    if (!iso && sscanf(text, "%2d:%2d:%2d%n", &hour, &minute, &second, &end) != 3)
        return 0;
    text += end;
    int fraction = 0;
    if (*text == '.') {
        ++text;
        for (int scale = 100; isdigit((unsigned char)*text); ++text) {
            fraction += (*text - '0') * scale;
            scale /= 10;
        }
    }
    zone[0] = '\0';
    if (iso) {
        if (strlen(text) >= 16) return 0;
        strcpy(zone, text);
    } else {
        end = 0;
        if (sscanf(text, "%15s %15s %15s %d %d%n", zone, weekday, month_name,
                   &day, &year, &end) != 5 || text[end]) return 0;
        for (month = 1; month <= 12 && !same_key(month_name, months[month - 1]); ++month) {}
    }
    if (year < 1970 || year > 9999 || month < 1 || month > 12 || day < 1 ||
        day > days_in_month[month - 1] + (month == 2 && leap(year)) ||
        hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59)
        return 0;
    long long days = (year - 1970) * 365LL;
    int previous_year = year - 1;
    days += previous_year / 4 - previous_year / 100 + previous_year / 400 - 477;
    for (int m = 1; m < month; ++m) days += days_in_month[m - 1] + (m == 2 && leap(year));
    *millis = (((days + day - 1) * 24 + hour) * 60 * 60 + minute * 60 + second) * 1000 + fraction;
    return 1;
}

static const char *release_party(const char *value, char *label, size_t size)
{
    static const char *sources[] = {
        NULL, "Calling party (PSTN)", "Calling party (VoIP)",
        "Called party (PSTN)", "Called party (VoIP)", "Internal POTS leg",
        "Internal VoIP leg", "Internal call-control application", "Internal VoIP AAA",
        "Console command", "External RADIUS server", "External management application",
        "External call-control agent"
    };
    if (!*value || !strcmp(value, "0")) return "Unknown";
    if (!digits(value)) return value;
    unsigned long code = strtoul(value, NULL, 10);
    const char *name = code > 0 && code < sizeof(sources)/sizeof(sources[0]) ?
                       sources[code] : "Unknown";
    snprintf(label, size, "%s [%s]", name, value);
    return label;
}

int read_record(char *const *field, int count, const Schema *header, Record *record)
{
    Schema inferred;
    if (!header) {
        if (!positional(field, count, &inferred)) return 0;
        header = &inferred;
    }
#define GET(key) at(field, count, header->key)
    const char *release = GET(release);
    const char *explicit_duration = GET(duration);
    char start_zone[16], end_zone[16];
    long long start, end;
    record->cell[SETUP] = GET(setup);
    record->cell[CALLING] = GET(calling);
    record->cell[CALLED] = GET(called);
    record->cell[CODEC] = GET(codec);
    record->cell[CAUSE] = GET(cause);
    record->cell[REASON] = GET(reason);
    record->cell[CONNECT] = GET(connect);
    record->cell[END] = GET(end);
    record->cell[LEG] = GET(leg);
    record->cell[CALL_ID] = GET(call_id);
    record->cell[CONFERENCE] = GET(conference);
    record->cell[SOURCE] = "";
#undef GET
    if (!*record->cell[SETUP] && !*record->cell[CALL_ID] && !*record->cell[END]) return 0;
    for (int i = 0; i < count && !*release; ++i)
        if (!strncmp(field[i], "release-source=", 15)) release = field[i] + 15;
    record->cell[RELEASE] = release_party(release, record->release, sizeof(record->release));
    record->cell[DURATION] = explicit_duration;
    if (!*explicit_duration && cdr_timestamp(record->cell[CONNECT], &start, start_zone) &&
        cdr_timestamp(record->cell[END], &end, end_zone) && !strcmp(start_zone, end_zone) && end >= start) {
        long long elapsed = end - start;
        snprintf(record->duration, sizeof(record->duration), "%lld.%03lld", elapsed / 1000, elapsed % 1000);
        record->cell[DURATION] = record->duration;
    }
    return 1;
}
