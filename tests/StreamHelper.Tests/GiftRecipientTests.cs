using System.Text.Json;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class GiftRecipientTests
{
    private static string Notice(string type, string gifterLogin, string gifterName, bool anonymous, string body) =>
        "{\"broadcaster_user_id\":\"777\",\"broadcaster_user_login\":\"streamer\",\"broadcaster_user_name\":\"Streamer\"," +
        "\"chatter_user_id\":\"42\",\"chatter_user_login\":\"" + gifterLogin + "\",\"chatter_user_name\":\"" + gifterName + "\",\"chatter_is_anonymous\":" + (anonymous ? "true" : "false") + "," +
        "\"color\":\"\",\"badges\":[],\"system_message\":\"\",\"message_id\":\"m-" + Guid.NewGuid().ToString("N") + "\"," +
        "\"message\":{\"text\":\"\",\"fragments\":[]},\"notice_type\":\"" + type + "\"," + body + "}";

    private static string Bundle(string gifter, string id, int total, bool anonymous = false) =>
        Notice("community_sub_gift", gifter, gifter, anonymous,
            "\"community_sub_gift\":{\"id\":\"" + id + "\",\"total\":" + total + ",\"sub_tier\":\"1000\",\"cumulative_total\":null}");

    private static string Single(string gifter, string recipient, string bundleId, bool anonymous = false) =>
        Notice("sub_gift", gifter, gifter, anonymous,
            "\"sub_gift\":{\"duration_months\":1,\"cumulative_total\":null,\"recipient_user_id\":\"9\",\"recipient_user_name\":\"" + recipient + "\"," +
            "\"recipient_user_login\":\"" + recipient.ToLowerInvariant() + "\",\"sub_tier\":\"1000\",\"community_gift_id\":" + (bundleId.Length > 0 ? "\"" + bundleId + "\"" : "null") + "}");

    private static GiftNotice Parse(string eventJson) =>
        EventSubParser.Parse(MockEventSubServer.Notification("n1", EventSubParser.ChatNoticeType, eventJson))!.Gift!;

    [Fact]
    public void Twitch_gift_notices_give_the_bundle_and_each_recipient()
    {
        var bundle = Parse(Bundle("kisumi_ex", "b-1", 5));
        var single = Parse(Single("kisumi_ex", "Shirouez", "b-1"));
        var alone = Parse(Single("kisumi_ex", "rxntigo", ""));
        var hidden = Parse(Single("", "Pika", "", anonymous: true));

        Assert.Equal((GiftNoticeKind.Bundle, "kisumi_ex", "b-1", 5), (bundle.Kind, bundle.GifterLogin, bundle.BundleId, bundle.Total));
        Assert.Equal((GiftNoticeKind.Recipient, "kisumi_ex", "b-1", "Shirouez"), (single.Kind, single.GifterLogin, single.BundleId, single.RecipientLabel));
        Assert.Equal("", alone.BundleId);
        Assert.True(hidden.Anonymous);
        Assert.Equal("", hidden.GifterLogin);
        Assert.Null(NoticeJson.Parse(NoticeJson.Raid("r", "x", "X", 3)).Gift);
    }

    private static Subscriber Gift(string key, string gifter, int total, DateTime at, bool anonymous = false) =>
        new() { Key = key, Kind = SubscriptionKind.Gift, Login = gifter, DisplayName = gifter, GiftTotal = total, IsAnonymous = anonymous, AtUtc = at };

    private static GiftNotice Recipient(string gifter, string name, string bundle, DateTime at, bool anonymous = false) =>
        new(GiftNoticeKind.Recipient, gifter, gifter, anonymous, bundle, 1, name.ToLowerInvariant(), name, "1000", at);

    [Fact]
    public void Recipients_of_a_bundle_go_to_the_gift_card_of_that_gifter()
    {
        var now = DateTime.UtcNow;
        var book = new GiftRecipientBook();
        var card = Gift("g", "kisumi_ex", 3, now);
        var other = Gift("o", "someone", 3, now);
        var cards = new[] { other, card };

        book.Add(cards, new GiftNotice(GiftNoticeKind.Bundle, "kisumi_ex", "kisumi_ex", false, "b-1", 3, "", "", "1000", now), now);
        foreach (var name in new[] { "Shirouez", "pikatherealone", "0uswwzspdndi" }) book.Add(cards, Recipient("kisumi_ex", name, "b-1", now), now);

        Assert.Equal(new[] { "Shirouez", "pikatherealone", "0uswwzspdndi" }, card.RecipientNames);
        Assert.Equal("b-1", card.GiftBundleId);
        Assert.False(other.HasRecipients);
        Assert.Equal("Кому: 3 ▸", card.RecipientsTitle);
        card.RecipientsOpen = true;
        Assert.Equal("Кому: 3 ▾", card.RecipientsTitle);
    }

    [Fact]
    public void Recipients_that_come_before_the_card_wait_for_it()
    {
        var now = DateTime.UtcNow;
        var book = new GiftRecipientBook();
        var cards = new List<Subscriber>();

        book.Add(cards, Recipient("kisumi_ex", "Shirouez", "b-1", now), now);
        book.Add(cards, Recipient("kisumi_ex", "Pika", "b-1", now), now);
        Assert.Equal(2, book.Waiting);

        var card = Gift("g", "kisumi_ex", 5, now);
        cards.Add(card);
        book.Retry(cards, now.AddSeconds(2));

        Assert.Equal(new[] { "Shirouez", "Pika" }, card.RecipientNames);
        Assert.Equal("Кому: 2 из 5 ▸", card.RecipientsTitle);
        Assert.Equal(0, book.Waiting);
    }

    [Fact]
    public void A_single_gift_finds_its_one_gift_card_and_not_a_bundle()
    {
        var now = DateTime.UtcNow;
        var book = new GiftRecipientBook();
        var bundle = Gift("b", "kisumi_ex", 5, now);
        var single = Gift("s", "kisumi_ex", 1, now);
        var cards = new[] { bundle, single };

        book.Add(cards, Recipient("kisumi_ex", "rxntigo", "", now), now);

        Assert.Equal(new[] { "rxntigo" }, single.RecipientNames);
        Assert.False(bundle.HasRecipients);
    }

    [Fact]
    public void Old_cards_full_cards_and_the_wrong_gifter_are_not_used()
    {
        var now = DateTime.UtcNow;
        var book = new GiftRecipientBook();
        var old = Gift("old", "kisumi_ex", 1, now.AddHours(-3));
        var full = Gift("full", "kisumi_ex", 1, now);
        full.AddRecipient("Someone", "");
        var stranger = Gift("x", "other", 1, now);
        var cards = new[] { old, full, stranger };

        book.Add(cards, Recipient("kisumi_ex", "rxntigo", "", now), now);

        Assert.False(old.HasRecipients);
        Assert.Equal(new[] { "Someone" }, full.RecipientNames);
        Assert.False(stranger.HasRecipients);
        Assert.Equal(1, book.Waiting);
        book.Retry(cards, now + GiftRecipientBook.Keep + TimeSpan.FromMinutes(1));
        Assert.Equal(0, book.Waiting);
    }

    [Fact]
    public void Anonymous_gifts_go_to_anonymous_cards_only()
    {
        var now = DateTime.UtcNow;
        var book = new GiftRecipientBook();
        var named = Gift("n", "kisumi_ex", 2, now);
        var hidden = Gift("a", "", 2, now, anonymous: true);
        var cards = new[] { named, hidden };

        book.Add(cards, Recipient("", "Pika", "b-9", now, anonymous: true), now);

        Assert.Equal(new[] { "Pika" }, hidden.RecipientNames);
        Assert.False(named.HasRecipients);
    }

    [Fact]
    public void The_same_recipient_is_not_listed_twice_and_the_list_survives_a_restart()
    {
        using var dir = new TempDir();
        var store = new SubscriberStore(dir.File("subscribers.json"));
        var card = Gift("g", "kisumi_ex", 3, DateTime.UtcNow);
        store.AddRange(new[] { card });

        Assert.True(card.AddRecipient("Shirouez", "b-1"));
        Assert.False(card.AddRecipient("shirouez", "b-1"));
        Assert.True(card.AddRecipient("Pika", "b-1"));
        var again = new SubscriberStore(dir.File("subscribers.json")).Items.Single();

        Assert.Equal(new[] { "Shirouez", "Pika" }, again.RecipientNames);
        Assert.Equal("b-1", again.GiftBundleId);
        Assert.DoesNotContain("RecipientsOpen", JsonSerializer.Serialize(card));
        Assert.DoesNotContain("Recipients", JsonSerializer.Serialize(Gift("plain", "x", 1, DateTime.UtcNow)));
    }

    [Fact]
    public void The_donation_toast_shows_the_video_picture_and_title()
    {
        var withVideo = new Donation { Username = "trevl", Amount = 200, Currency = "RUB", Message = "смотри" };
        withVideo.AttachVideo("C1CVrXTIqvc", "ТЕСТ НА ШИЗУ", "https://www.youtube.com/watch?v=C1CVrXTIqvc");
        var otherSite = new Donation { Username = "trevl", Amount = 200, Currency = "RUB" };
        otherSite.AttachVideo("", "Клип", "https://example.com/clip");
        var plain = new Donation { Username = "trevl", Amount = 200, Currency = "RUB" };

        var toast = EventToasts.Donations(new[] { withVideo });
        Assert.Equal(new ToastText("Новый донат", "trevl · 200 ₽", "смотри", "https://i.ytimg.com/vi/C1CVrXTIqvc/mqdefault.jpg", "ТЕСТ НА ШИЗУ"), toast);
        Assert.Equal(new ToastText("Новый донат", "trevl · 200 ₽", "▶ Клип"), EventToasts.Donations(new[] { otherSite }));
        Assert.Equal(new ToastText("Новый донат", "trevl · 200 ₽", null), EventToasts.Donations(new[] { plain }));
        Assert.Equal("Видео к донату", EventToasts.LateVideo(withVideo).Title);
        Assert.Equal(toast.Picture, EventToasts.LateVideo(withVideo).Picture);
    }

    private sealed class FakeSink : IToastSink
    {
        public readonly List<ToastText> Shown = new();
        public readonly List<ToastText> Updated = new();

        public object Show(ToastText text)
        {
            Shown.Add(text);
            return Shown.Count;
        }

        public bool Update(object toast, ToastText text)
        {
            Updated.Add(text);
            return true;
        }
    }

    [Fact]
    public void A_video_that_comes_later_is_put_into_the_open_donation_toast()
    {
        var sink = new FakeSink();
        var merger = new ToastMerger(sink);
        var donation = new Donation { Username = "trevl", Amount = 200, Currency = "RUB" };
        var other = new Donation { Username = "x", Amount = 1, Currency = "RUB" };
        merger.Show(new[] { donation }, EventToasts.Donations);

        donation.AttachVideo("C1CVrXTIqvc", "ТЕСТ", "https://www.youtube.com/watch?v=C1CVrXTIqvc");

        Assert.True(merger.Refresh(donation, EventToasts.Donations));
        Assert.Equal("https://i.ytimg.com/vi/C1CVrXTIqvc/mqdefault.jpg", sink.Updated.Single().Picture);
        Assert.False(merger.Refresh(other, EventToasts.Donations));
    }
}
