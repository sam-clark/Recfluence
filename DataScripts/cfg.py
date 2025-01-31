
from typing import List, Optional
from dataclasses import dataclass
from dataclasses_json.api import DataClassJsonMixin
from dotenv import load_dotenv
import os
import aiohttp
import socket


@dataclass
class StoreCfg:
    dataStorageCs: str
    container: str


@dataclass
class SnowflakeCfg:
    creds: str
    host: str
    db: str
    warehouse: Optional[str] = None
    schema: Optional[str] = None
    role: Optional[str] = None


@dataclass
class SeqCfg:
    seqUrl: str


@dataclass
class DataScriptsCfg:
    spacyBatchSize: int = 800


@dataclass
class RunState(DataClassJsonMixin):
    videoPaths: Optional[List[str]] = None


@dataclass
class Cfg(DataClassJsonMixin):
    snowflake: SnowflakeCfg
    storage: StoreCfg
    seq: SeqCfg
    dataScripts: DataScriptsCfg = DataScriptsCfg()
    state: RunState = RunState()
    env: Optional[str] = 'dev'
    branchEnv: Optional[str] = None
    machine: Optional[str] = None
    localDir: Optional[str] = None  # if provided will use local file storage isstead


async def load_cfg() -> Cfg:
    '''loads application configuration form a blob from the cfg_sas environment variable'''
    load_dotenv()
    cfg_sas = os.getenv('cfg_sas')
    if(cfg_sas is None):
        raise Exception('cfg_sas environment variable is required. Add a .env file with the sas url to a recfluence app config file')

    cfg: Cfg
    async with aiohttp.ClientSession() as sesh:
        async with sesh.get(cfg_sas) as r:
            json = await r.text()
            cfg = Cfg.from_json(json)

    cfg.env = os.getenv('env') or cfg.env or 'dev'
    cfg.branchEnv = os.getenv('branch_env') or cfg.branchEnv
    runStateJson = os.getenv('run_state')
    cfg.state = RunState.from_json(runStateJson) if runStateJson else RunState()
    cfg.machine = os.getenv('AzureContainers_Container') or socket.gethostname()
    cfg.localDir = os.getenv('local_dir')

    if(cfg.branchEnv != None):
        cfg.storage.container = f'{cfg.storage.container }-{cfg.branchEnv}'
        cfg.snowflake.db = f'{cfg.snowflake.db }_{cfg.branchEnv}'

    return cfg
