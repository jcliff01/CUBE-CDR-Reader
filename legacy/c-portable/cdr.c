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

static const char *endpoint_keys[][3] = {
    {"remote-signaling-ip", "h323-remote-address", "remote-ip"},
    {"remote-signaling-port", "remote-sip-port", NULL},
    {"remote-udp-port", NULL, NULL},
    {"remote-media-address", "remote-media-ip", "remote-rtp-ip"},
    {"remote-media-udp-port", "remote-media-port", "remote-rtp-port"},
    {"local-signaling-ip", "local-ip", "local-address"},
    {"local-signaling-port", "local-sip-port", NULL},
    {"local-media-address", "local-media-ip", "local-rtp-ip"},
    {"local-media-udp-port", "local-media-port", "local-rtp-port"}
};
_Static_assert(LOCAL_MEDIA_PORT - REMOTE_IP + 1 ==
               sizeof(endpoint_keys) / sizeof(endpoint_keys[0]), "endpoint columns");

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
        COL("h323-conf-id","conference-id",NULL),
        {0},
        COL("forwarded","call-forwarded","is-forwarded"),
        COL("forwarded-to","forward-to","fwdto"),
        COL("feature-id_field1","feature-name","feature"),
        COL("feature-id_field3","feature-status","frs"),
        COL("feature-id_field11",NULL,NULL)
    };
#undef COL
    if (candidate.setup < 0 && candidate.end < 0) return 0;
    if (candidate.calling < 0 && candidate.called < 0 && candidate.call_id < 0) return 0;
    for (int i = 0; i < LOCAL_MEDIA_PORT - REMOTE_IP + 1; ++i)
        candidate.endpoint[i] = locate(field, count, endpoint_keys[i][0],
                                       endpoint_keys[i][1], endpoint_keys[i][2]);
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

static int forward_feature(const char *feature)
{
    return same_key(feature, "CFA") || same_key(feature, "CFNA") || same_key(feature, "CFBY");
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
    int compact_feature = count > cause + 13 && strchr(field[cause + 13], ':') &&
                          *field[cause + 12] && !digits(field[cause + 12]);
    int detailed = !compact_feature && count > cause + 23;
    int ports = cause + 25 + (cause != 9 && codec == cause + 24);
    if (!detailed || digits(at(field, count, codec))) codec = -1;
    int feature = -1;
    for (int i = cause + 12; i + 1 < count; ++i) {
        if (i > cause + 12 && i < 100) continue;
        if (forward_feature(field[i]) && strchr(field[i + 1], ':')) {
            feature = i;
            break;
        }
    }
    *schema = (Schema){
        setup, cause + 10, cause + 11, -1, codec, -1, cause, cause + 1,
        cause - 2, cause - 1, cause + 2, 1, 4,
        {-1, -1, detailed ? ports : -1, detailed ? cause + 60 : -1,
         detailed ? ports + 1 : -1, -1, -1, -1, -1},
        -1, -1, feature, feature < 0 ? -1 : feature + 2, feature < 0 ? -1 : feature + 10
    };
    return 1;
}

static const char *attribute(const char *value, const char *key)
{
    const char *equal = strchr(value, '=');
    if (!equal) return "";
    size_t length = (size_t)(equal - value);
    if (length >= 64) return "";
    char name[64];
    memcpy(name, value, length);
    name[length] = '\0';
    return same_key(name, key) ? equal + 1 : "";
}

static int port_like(const char *value)
{
    if (!digits(value)) return 0;
    unsigned long port = strtoul(value, NULL, 10);
    return port > 0 && port <= 65535;
}

static int ip_like(const char *value)
{
    if (strchr(value, ':'))
        return strspn(value, "0123456789abcdefABCDEF:.") == strlen(value);
    int a, b, c, d, end = 0;
    return sscanf(value, "%3d.%3d.%3d.%3d%n", &a, &b, &c, &d, &end) == 4 &&
           !value[end] && (unsigned)a <= 255 && (unsigned)b <= 255 &&
           (unsigned)c <= 255 && (unsigned)d <= 255;
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
    int native = header == NULL;
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
    const char *feature = GET(feature);
    const char *feature_status = GET(feature_status);
    const char *flag = GET(forwarded);
    const char *target = GET(forward_to);
    if (!*target && forward_feature(feature)) target = GET(feature_target);
    for (int i = 0; i < LOCAL_MEDIA_PORT - REMOTE_IP + 1; ++i)
        record->cell[REMOTE_IP + i] = at(field, count, header->endpoint[i]);
#undef GET
    if (!*record->cell[SETUP] && !*record->cell[CALL_ID] && !*record->cell[END]) return 0;
    for (int i = 0; i < count && !*release; ++i)
        if (!strncmp(field[i], "release-source=", 15)) release = field[i] + 15;
    if (native) {
        if (!ip_like(record->cell[REMOTE_MEDIA_IP])) record->cell[REMOTE_MEDIA_IP] = "";
        if (!port_like(record->cell[REMOTE_UDP_PORT])) record->cell[REMOTE_UDP_PORT] = "";
        if (!port_like(record->cell[REMOTE_MEDIA_PORT])) record->cell[REMOTE_MEDIA_PORT] = "";
    }
    for (int i = 0; i < count; ++i) {
        if (!strchr(field[i], '=')) continue;
        for (int col = 0; col < LOCAL_MEDIA_PORT - REMOTE_IP + 1; ++col) {
            if (*record->cell[REMOTE_IP + col]) continue;
            for (int k = 0; k < 3 && endpoint_keys[col][k]; ++k) {
                const char *value = attribute(field[i], endpoint_keys[col][k]);
                if (*value) {
                    record->cell[REMOTE_IP + col] = value;
                    break;
                }
            }
        }
    }
    if (same_key(flag, "yes") || same_key(flag, "true") || !strcmp(flag, "1"))
        record->cell[FORWARDED] = "Yes";
    else if (same_key(flag, "no") || same_key(flag, "false") || !strcmp(flag, "0"))
        record->cell[FORWARDED] = "No";
    else if (forward_feature(feature))
        record->cell[FORWARDED] = !strcmp(feature_status, "0") ? "Yes" : "Forwarding event";
    else
        record->cell[FORWARDED] = "Unknown";
    record->cell[FORWARDED_TO] = target;
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
