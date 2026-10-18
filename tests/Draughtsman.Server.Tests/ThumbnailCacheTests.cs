using Draughtsman.Server.Thumbnails;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The thumbnail cache's bounds and eviction order (Thumbnails/ThumbnailCache.cs). Every bound is a count of entries or bytes, so
/// no test here depends on a clock or on how busy the machine is.
/// </summary>
public class ThumbnailCacheTests
{
    [Fact]
    public void A_stored_thumbnail_is_found_under_its_own_key_and_no_other()
    {
        var cache = new ThumbnailCache(10, 1000);
        string key = ThumbnailCache.KeyOf("doc_a", "dv1:aa", "v1");

        cache.Set("doc_a", key, Svg("doc_a", 10));

        Assert.NotNull(cache.TryGet(key));
        Assert.Null(cache.TryGet(ThumbnailCache.KeyOf("doc_a", "dv1:bb", "v1")));
        Assert.Null(cache.TryGet(ThumbnailCache.KeyOf("doc_a", "dv1:aa", "v2")));
        Assert.Null(cache.TryGet(ThumbnailCache.KeyOf("doc_b", "dv1:aa", "v1")));
    }

    [Fact]
    public void The_key_cannot_be_forged_by_moving_a_character_between_its_parts()
    {
        Assert.NotEqual(ThumbnailCache.KeyOf("ab", "c", "d"), ThumbnailCache.KeyOf("a", "bc", "d"));
        Assert.NotEqual(ThumbnailCache.KeyOf("a", "bc", "d"), ThumbnailCache.KeyOf("a", "b", "cd"));
    }

    [Fact]
    public void The_entry_bound_evicts_the_least_recently_used_first()
    {
        var cache = new ThumbnailCache(3, 1_000_000);
        foreach (string id in new[] { "a", "b", "c" })
        {
            cache.Set(id, Key(id), Svg(id, 10));
        }

        // Reading "a" makes "b" the oldest.
        Assert.NotNull(cache.TryGet(Key("a")));
        cache.Set("d", Key("d"), Svg("d", 10));

        Assert.Equal(3, cache.Count);
        Assert.Null(cache.TryGet(Key("b")));
        Assert.NotNull(cache.TryGet(Key("a")));
        Assert.NotNull(cache.TryGet(Key("c")));
        Assert.NotNull(cache.TryGet(Key("d")));
    }

    [Fact]
    public void The_byte_bound_evicts_until_the_total_fits_and_the_entry_bound_is_not_what_stopped_it()
    {
        var cache = new ThumbnailCache(100, 100);
        cache.Set("a", Key("a"), Svg("a", 40));
        cache.Set("b", Key("b"), Svg("b", 40));

        cache.Set("c", Key("c"), Svg("c", 40));

        Assert.Equal(2, cache.Count);
        Assert.Equal(80, cache.Bytes);
        Assert.Null(cache.TryGet(Key("a")));
        Assert.NotNull(cache.TryGet(Key("b")));
        Assert.NotNull(cache.TryGet(Key("c")));
    }

    [Fact]
    public void One_large_entry_can_push_out_several_small_ones()
    {
        var cache = new ThumbnailCache(100, 100);
        foreach (string id in new[] { "a", "b", "c" })
        {
            cache.Set(id, Key(id), Svg(id, 30));
        }

        cache.Set("big", Key("big"), Svg("big", 90));

        Assert.Equal(1, cache.Count);
        Assert.Equal(90, cache.Bytes);
        Assert.NotNull(cache.TryGet(Key("big")));
    }

    [Fact]
    public void A_thumbnail_bigger_than_the_whole_byte_bound_is_not_kept_and_pushes_nothing_out()
    {
        var cache = new ThumbnailCache(100, 100);
        cache.Set("a", Key("a"), Svg("a", 40));

        cache.Set("huge", Key("huge"), Svg("huge", 101));

        Assert.Null(cache.TryGet(Key("huge")));
        Assert.NotNull(cache.TryGet(Key("a")));
        Assert.Equal(40, cache.Bytes);
    }

    [Fact]
    public void A_document_keeps_one_entry_so_an_edit_frees_the_version_it_replaced()
    {
        var cache = new ThumbnailCache(100, 1000);
        string first = ThumbnailCache.KeyOf("doc_a", "dv1:aa", "v1");
        string second = ThumbnailCache.KeyOf("doc_a", "dv1:bb", "v1");
        cache.Set("doc_a", first, Svg("doc_a", 50));

        cache.Set("doc_a", second, Svg("doc_a", 70));

        Assert.Equal(1, cache.Count);
        Assert.Equal(70, cache.Bytes);
        Assert.Null(cache.TryGet(first));
        Assert.NotNull(cache.TryGet(second));
    }

    [Fact]
    public void Setting_the_same_key_twice_replaces_it_without_counting_it_twice()
    {
        var cache = new ThumbnailCache(100, 1000);

        cache.Set("a", Key("a"), Svg("a", 50));
        cache.Set("a", Key("a"), Svg("a", 60));

        Assert.Equal(1, cache.Count);
        Assert.Equal(60, cache.Bytes);
    }

    [Fact]
    public void The_bounds_hold_under_concurrent_use()
    {
        var cache = new ThumbnailCache(20, 10_000);

        Parallel.For(0, 2000, i =>
        {
            string id = "doc_" + (i % 60);
            cache.Set(id, ThumbnailCache.KeyOf(id, "v" + i, "x"), Svg(id, 100 + (i % 7)));
            cache.TryGet(ThumbnailCache.KeyOf(id, "v" + (i - 1), "x"));
        });

        Assert.InRange(cache.Count, 1, 20);
        Assert.True(cache.Bytes <= 10_000);
    }

    [Fact]
    public void A_bound_that_is_not_positive_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThumbnailCache(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThumbnailCache(10, 0));
    }

    private static string Key(string id) => ThumbnailCache.KeyOf(id, "dv1:v", "var");

    private static Thumbnail Svg(string id, int bytes) => new(new byte[bytes], "dv1:v", "var", ThumbnailKind.Full, Degraded: false);
}
