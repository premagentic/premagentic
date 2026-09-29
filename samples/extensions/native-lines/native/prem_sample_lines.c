/*
 * The native half of the native-lines sample extension: one function, no C
 * library, so the same file builds for every platform with nothing to link.
 * scripts/build-native-sample.sh builds it; the built files are committed
 * beside this one.
 *
 * Returns the index of the first line feed at or after start in the UTF-16
 * text, or length when there is none.
 */
#if defined(_WIN32)
#define PREM_EXPORT __declspec(dllexport)
#else
#define PREM_EXPORT __attribute__((visibility("default")))
#endif

PREM_EXPORT int prem_sample_line_end(const unsigned short *text, int length, int start)
{
    for (int i = start; i < length; i++)
        if (text[i] == 10)
            return i;
    return length;
}
