﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Google;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Humanizer;
using Polly;
using Serilog;
using SysExtensions;
using SysExtensions.Collections;
using SysExtensions.Net;
using SysExtensions.Text;

namespace YtReader.YtApi {
  public class YtClient {
    public YtClient(YtApiCfg cfg, ILogger log) {
      Log = log;
      YtService = new();
      AvailableKeys = new(cfg.Keys.Select(k => new KeyValuePair<string, string>(k, value: null)));
      Start = DateTime.UtcNow;
    }

    public ConcurrentDictionary<string, string> AvailableKeys { get; set; }
    public DateTime                             Start         { get; }
    ILogger                                     Log           { get; }
    public YouTubeService                       YtService     { get; }

    async Task<T> GetResponse<T>(YouTubeBaseServiceRequest<T> request) {
      void SetRequestKey() {
        if (AvailableKeys.Count == 0)
          throw new InvalidOperationException("Ran out of quota for all available keys");
        request.Key = AvailableKeys.First().Key;
      }

      SetRequestKey();
      return await Policy
        // handle quote limits
        .Handle<GoogleApiException>(g => {
          var isQuotaError = g.HttpStatusCode == HttpStatusCode.Forbidden && g.Error.Errors.Any(e => e.Reason == "quotaExceeded");
          if (!isQuotaError) {
            Log.Debug(g, "YtApi - return error not detected as quota: {Errors}", g.Error.Errors.Join(", ", e => e.Reason));
            return false;
          }
          AvailableKeys.TryRemove(request.Key, out _);
          Log.Warning(g, "Quota exceeded, no longer using key {Key}", request.Key);
          SetRequestKey();
          return true;
        })
        .RetryForeverAsync()
        // wrap generic transient fault handling
        .WrapAsync(Policy
          .Handle<HttpRequestException>()
          .Or<GoogleApiException>(g => g.HttpStatusCode.IsTransient())
          .WaitAndRetryAsync(6, i => i.ExponentialBackoff(1.Seconds())))
        .ExecuteAsync(request.ExecuteAsync);
    }

    #region Videos

    VideoData ToVideoData(Video v) {
      var r = new VideoData {
        VideoId = v.Id,
        VideoTitle = v.Snippet.Title,
        Description = v.Snippet.Description,
        ChannelTitle = v.Snippet.ChannelTitle,
        ChannelId = v.Snippet.ChannelId,
        Language = v.Snippet.DefaultLanguage,
        PublishedAt = v.Snippet.PublishedAt?.ParseDate() ?? DateTime.MinValue,
        CategoryId = v.Snippet.CategoryId,
        Stats = new() {
          Views = v.Statistics?.ViewCount,
          Likes = v.Statistics?.LikeCount,
          Dislikes = v.Statistics?.DislikeCount,
          Updated = DateTime.UtcNow
        },
        Updated = DateTime.UtcNow
      };
      if (v.Snippet.Tags != null)
        r.Tags.AddRange(v.Snippet.Tags);
      if (v.TopicDetails?.RelevantTopicIds != null)
        r.Topics.AddRange(v.TopicDetails.RelevantTopicIds);

      return r;
    }

    public async Task<VideoData> VideoData(string id) {
      var s = YtService.Videos.List("snippet,topicDetails,statistics");
      s.Id = id;

      VideoListResponse response;
      try {
        response = await GetResponse(s);
      }
      catch (GoogleApiException ex) {
        Log.Error("Error {ex} VideoData for {VideoId} ", ex, id);
        return null;
      }

      var v = response.Items.FirstOrDefault();
      if (v == null) return null;

      var data = ToVideoData(v);

      return data;
    }

    public async Task<ICollection<RecommendedVideoListItem>> GetRelatedVideos(string id) {
      var s = YtService.Search.List("snippet");
      s.RelatedToVideoId = id;
      s.Type = "video";
      s.MaxResults = 20;

      SearchListResponse response;
      try {
        response = await GetResponse(s);
      }
      catch (GoogleApiException ex) {
        Log.Error("Error {ex} GetRelatedVideos for {VideoId} ", ex, id);
        return null;
      }

      var vids = new List<RecommendedVideoListItem>();
      var rank = 1;
      foreach (var item in response.Items) {
        vids.Add(new() {
          VideoId = item.Id.VideoId,
          VideoTitle = item.Snippet.Title,
          ChannelId = item.Snippet.ChannelId,
          ChannelTitle = item.Snippet.ChannelTitle,
          Rank = rank
        });

        rank++;
      }

      return vids;
    }

    #endregion

    #region Channels

    /// <summary>The most popular in that channel. Video's do not include related data.</summary>
    public async Task<ICollection<ChannelVideoListItem>> VideosInChannel(ChannelData c, DateTime publishedAfter,
      DateTime? publishBefore = null) {
      var s = YtService.Search.List("snippet");
      s.ChannelId = c.Id;
      s.PublishedAfter = publishedAfter;
      s.PublishedBefore = publishBefore;
      s.MaxResults = 50;
      s.Order = SearchResource.ListRequest.OrderEnum.Date;
      s.Type = "video";

      var vids = new List<ChannelVideoListItem>();
      while (true) {
        var res = await GetResponse(s);
        vids.AddRange(res.Items.Where(v => v.Snippet.PublishedAt != null).Select(v => new ChannelVideoListItem {
          VideoId = v.Id.VideoId,
          VideoTitle = v.Snippet.Title,
          PublishedAt = v.Snippet.PublishedAt.ParseDate(),
          Updated = DateTime.UtcNow
        }));
        if (res.NextPageToken == null)
          break;
        s.PageToken = res.NextPageToken;
      }

      return vids;
    }

    public async Task<ChannelData> ChannelData(string id, bool full = false) {
      var channelList = YtService.Channels.List(new[] {"snippet", "statistics"}.Concat(full ? "brandingSettings" : null).NotNull().Join(","));
      channelList.Id = id;
      var response = await GetResponse(channelList);
      var c = response.Items?.FirstOrDefault();
      if (c == null) return null;

      SubscriptionListResponse subRes = null;
      if (full) {
        var subs = YtService.Subscriptions.List("snippet");
        subs.ChannelId = id;
        try {
          subRes = await GetResponse(subs);
        }
        catch (Exception) {
          Log.Debug("YtApi - getting channel {Channel} subs failed. most don't allow it so this is fine", id);
        }
      }

      var data = new ChannelData {
        Id = id,
        Title = c.Snippet.Title,
        Description = c.Snippet.Description,
        Country = c.Snippet.Country,
        Thumbnails = c.Snippet.Thumbnails,
        Stats = new() {
          ViewCount = c.Statistics.ViewCount,
          SubCount = c.Statistics.SubscriberCount,
          Updated = DateTime.UtcNow
        },
        FeaturedChannelIds = c.BrandingSettings?.Channel?.FeaturedChannelsUrls?.ToArray(),
        DefaultLanguage = c.BrandingSettings?.Channel?.DefaultLanguage,
        Keywords = c.BrandingSettings?.Channel?.Keywords,
        Subscriptions = subRes?.Items?.Select(s => new ChannelSubscription {Id = s.Snippet?.ChannelId, Title = s.Snippet?.Title}).ToArray()
      };
      return data;
    }

    #endregion
  }

  public class ChannelData {
    public string                Id                 { get; set; }
    public string                Title              { get; set; }
    public string                Country            { get; set; }
    public string                Description        { get; set; }
    public ThumbnailDetails      Thumbnails         { get; set; }
    public ChannelStats          Stats              { get; set; }
    public string[]              FeaturedChannelIds { get; set; }
    public string                DefaultLanguage    { get; set; }
    public string                Keywords           { get; set; }
    public ChannelSubscription[] Subscriptions      { get; set; }

    public override string ToString() => Title;
  }

  public class ChannelSubscription {
    public string Id    { get; set; }
    public string Title { get; set; }
  }

  public class ChannelStats {
    public ulong?   ViewCount { get; set; }
    public ulong?   SubCount  { get; set; }
    public DateTime Updated   { get; set; }
  }

  public class VideoData : ChannelVideoListItem {
    public string Description  { get; set; }
    public string ChannelTitle { get; set; }
    public string ChannelId    { get; set; }
    public string Language     { get; set; }

    public string CategoryId { get; set; }

    public ICollection<string> Topics { get; } = new List<string>();
    public ICollection<string> Tags   { get; } = new List<string>();

    public VideoStats Stats { get; set; } = new();

    public override string ToString() => $"{ChannelTitle} {VideoTitle}";
  }

  public class VideoStats {
    public ulong?   Views    { get; set; }
    public ulong?   Likes    { get; set; }
    public ulong?   Dislikes { get; set; }
    public DateTime Updated  { get; set; }
  }

  public class VideoItem {
    public string VideoId    { get; set; }
    public string VideoTitle { get; set; }

    public override string ToString() => VideoTitle;
  }

  public class RecommendedVideoListItem : VideoItem {
    public string ChannelTitle { get; set; }
    public string ChannelId    { get; set; }
    public int    Rank         { get; set; }

    public override string ToString() => $"{Rank}. {ChannelTitle}: {VideoTitle}";
  }

  public class ChannelVideoListItem : VideoItem {
    public DateTime PublishedAt { get; set; }
    public DateTime Updated     { get; set; }
  }
}