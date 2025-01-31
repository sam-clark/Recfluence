from logging import Logger
import tempfile
import time
import secrets

from spacy.language import Language
from args import Args, load_args
from datetime import datetime, timezone
import gzip
from itertools import chain
from dataclasses_json.api import DataClassJsonMixin
from dataclasses_json import dataclass_json
from marshmallow.fields import DateTime
from log import configure_log
import jsonl
from blobstore import BlobStore
from pathlib import Path, PurePath
from typing import Callable, Iterable, List, Optional, TypeVar
from snowflake.connector.cursor import SnowflakeCursor
import spacy
from sf import sf_connect
from cfg import Cfg, load_cfg
import asyncio
from dataclasses import dataclass


@dataclass
class DbVideoEntity:
    videoId: str
    videoTitle: str
    descripton: Optional[str]
    captions: Optional[str]
    videoUpdated: DateTime
    captionUpdated: Optional[DateTime]


@dataclass_json
@dataclass
class DbCaption(DataClassJsonMixin):
    offset: Optional[int] = None
    caption: Optional[str] = None


@dataclass
class VideoCaption:
    video: DbVideoEntity
    offset: Optional[int] = None
    caption: Optional[str] = None


@dataclass
class Entity:
    name: str
    type: str
    start_char: int
    end_char: int


@dataclass
class VideoEntity:
    videoId: str
    part: str  # title, description, captions
    offset: Optional[int]
    entities: List[Entity]
    videoUpdated: DateTime
    captionUpdated: Optional[DateTime]
    updated: DateTime


T = TypeVar('T')

EXCLUDE_LABELS = ['CARDINAL', 'MONEY', 'DATE', 'TIME']


def get_ents(pipe_res) -> List[Entity]:
    return list(map(lambda r: list([Entity(ent.text.strip(), ent.label_) for ent in r.ents]), pipe_res))


def get_entities(lang: Language, rows: List[T], getVal: Callable[[T], str] = None) -> Iterable[Iterable[Entity]]:
    res = list(lang.pipe([(getVal(r) if getVal is not None else r) or "" for r in rows], n_process=4))
    return map(lambda r: [Entity(e.text.strip(), e.label_, e.start_char, e.end_char)
                          for e in r.ents if e.label_ not in EXCLUDE_LABELS], res)


def get_language():
    return spacy.load("en_core_web_sm", disable=['parser', 'tagger', 'textcat', 'lemmatizer'])


def video_entities(cfg: Cfg, args: Args, log: Logger):
    blob = BlobStore(cfg.storage)
    lang = get_language()

    def entities(rows: List[T], getVal: Callable[[T], str]):
        return get_entities(lang, rows, getVal)

    localBasePath = Path(tempfile.gettempdir()) / 'data_scripts' if cfg.localDir is None else Path(cfg.localDir)
    localPath = localBasePath / 'video_entities'
    localPath.mkdir(parents=True, exist_ok=True)

    db = sf_connect(cfg.snowflake)
    try:

        videoSql = ','.join([f"'{v}'" for v in args.videos]) if args.videos else None

        selects = list([f'select $1:video_id::string video_id from @public.yt_data/{p}' for p in cfg.state.videoPaths]) \
            if cfg.state.videoPaths else list([f'select video_id from video_latest where video_id in ({videoSql})'])

        batchTotal = len(selects)
        batchNum = 0
        for select in selects:
            batchNum = batchNum + 1
            cur: SnowflakeCursor = db.cursor()
            sql = f'''
with
load as ({select})
, vids as (
select v.video_id, v.video_title, v.description, v.updated
from load l
join video_latest v on v.video_id = l.video_id
order by video_id
)
, s as (
select v.video_id
    , any_value(video_title) video_title
    , any_value(description) description
    , array_agg(object_construct('offset',s.offset_seconds::int,'caption',s.caption)) within group ( order by offset_seconds ) captions
    , max(v.updated) video_updated
    , max(s.updated) caption_updated
from vids v
    left join caption s on v.video_id=s.video_id
    group by v.video_id
)
select * from s
            '''

            log.info('video_entities - getting data for this video file batch {batch}/{batchTotal}: {sql}',
                     sql=sql, batch=batchNum, batchTotal=batchTotal)
            sqlRes = cur.execute(sql)
            videoTotal = sqlRes.rowcount

            log.debug('video_entities - processing entities')

            def captions(json) -> List[DbCaption]:
                return DbCaption.schema().loads(json, many=True)

            videoCount = 0
            while True:
                raw_rows = cur.fetchmany(cfg.dataScripts.spacyBatchSize)
                if(len(raw_rows) == 0):
                    break
                videoCount = videoCount + len(raw_rows)
                source_videos = list(map(lambda r: DbVideoEntity(r[0], r[1], r[2], r[3], r[4], r[5]), raw_rows))
                title_entities = entities(source_videos, lambda r: r.videoTitle)
                description_entities = entities(source_videos, lambda r: r.descripton)
                source_captions = [
                    VideoCaption(r, c.offset, c.caption) for r in source_videos
                    for c in captions(r.captions)
                ]
                caption_entities = entities(source_captions, lambda r: r.caption)

                updated = datetime.now(timezone.utc)
                caption_rows = map(lambda r, t: VideoEntity(r.video.videoId, 'caption', r.offset, t, r.video.videoUpdated,
                                                            r.video.captionUpdated, updated), source_captions, caption_entities)
                res_rows = list(chain(map(lambda r, t: VideoEntity(r.videoId, 'title', None, t, r.videoUpdated, r.captionUpdated, updated), source_videos, title_entities),
                                      map(lambda r, t: VideoEntity(r.videoId, 'description', None, t, r.videoUpdated,
                                                                   r.captionUpdated, updated), source_videos, description_entities),
                                      [r for r in caption_rows if r.offset is not None or r.entities is not None]))

                fileName = f'{time.strftime("%Y-%m-%d_%H-%M-%S")}.{secrets.token_hex(5)[:5]}.jsonl.gz'
                localFile = localPath / fileName
                blobFile = PurePath(f'db2/video_entities/{fileName}') if cfg.localDir is None else None
                with gzip.open(localFile, 'wb') as f:
                    jsonl.dump(res_rows, f, cls=jsonl.JsonlEncoder)
                if(blobFile):
                    blob.save_file(localFile, blobFile)

                log.info('video_entities - saved {file} {videoCount}/{videoTotal} videos in batch {batch}/{batchTotal}',
                         videoCount=videoCount, videoTotal=videoTotal, file=str(blobFile) if blobFile else str(localFile),
                         batch=batchNum, batchTotal=batchTotal)

            cur.close()

    finally:
        db.close()
