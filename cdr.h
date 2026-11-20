#ifndef CDR_H
#define CDR_H

#include <stddef.h>

enum Column {
    SETUP, CALLING, CALLED, DURATION, CODEC, RELEASE, CAUSE, REASON,
    CONNECT, END, LEG, CALL_ID, CONFERENCE, SOURCE, COLUMN_COUNT
};

enum { MAX_FIELDS = 512 };
typedef struct {
    int setup, calling, called, duration, codec, release, cause, reason;
    int connect, end, leg, call_id, conference;
} Schema;

typedef struct {
    const char *cell[COLUMN_COUNT];
    char duration[40];
    char release[96];
} Record;

int cdr_timestamp(const char *text, long long *millis, char zone[16]);
int split_csv(char *line, char *field[MAX_FIELDS]);
int read_header(char *const *field, int count, Schema *schema);
int read_record(char *const *field, int count, const Schema *schema, Record *record);
#endif
