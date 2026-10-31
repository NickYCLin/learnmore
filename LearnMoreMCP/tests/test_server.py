import asyncio
import json
import os
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from uuid import uuid4

import httpx
import pytest
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from learnmore_music_mcp.server import MusicClient

READ = 'r' * 40
WRITE = 'w' * 40

@pytest.mark.parametrize('url', ['http://example.invalid', 'https://user:secret@example.invalid', 'file:///tmp/api', 'https://example.invalid?q=token', 'https://example.invalid/#token'])
def test_rejects_unsafe_base_urls(url):
    with pytest.raises(ValueError):
        MusicClient({'base_url':url,'read_token':READ})


def test_remote_error_never_echoes_credentials_or_body():
    secret='DO_NOT_PRINT_THIS_CREDENTIAL'
    def handler(request):
        assert request.headers['Authorization']=='Bearer '+READ
        return httpx.Response(503,json={'error':secret,'detail':'Bearer '+READ})
    client=MusicClient({'base_url':'https://example.invalid/LearnMore','read_token':READ},httpx.MockTransport(handler))
    with pytest.raises(ValueError) as error:
        asyncio.run(client.request('GET','jobs/'+str(uuid4())))
    assert secret not in str(error.value)
    assert READ not in str(error.value)


def test_preserves_actionable_conflict_codes():
    client=MusicClient({'base_url':'https://example.invalid','read_token':READ},httpx.MockTransport(lambda req:httpx.Response(409,json={'error':'song_changed_since_analysis'})))
    with pytest.raises(ValueError,match='song_changed_since_analysis'):
        asyncio.run(client.request('POST','jobs/'+str(uuid4())+'/apply'))


def test_cannot_reuse_processing_credential_for_apply():
    with pytest.raises(ValueError):
        MusicClient({'base_url':'https://example.invalid','read_token':READ,'write_token':READ})


@pytest.mark.parametrize('can_write',[False,True])
def test_real_stdio_protocol_and_authenticated_http_tools(tmp_path,can_write):
    calls=[]
    jid=str(uuid4())
    class Handler(BaseHTTPRequestHandler):
        def log_message(self,*args):pass
        def do_GET(self):self.respond()
        def do_POST(self):self.respond()
        def respond(self):
            payload=json.loads(self.rfile.read(int(self.headers.get('Content-Length','0'))) or b'{}')
            calls.append((self.command,self.path,self.headers.get('Authorization'),payload))
            self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers()
            self.wfile.write(json.dumps({'jobId':jid,'state':'verified','previewHash':'A'*64}).encode())
    api=ThreadingHTTPServer(('127.0.0.1',0),Handler)
    thread=threading.Thread(target=api.serve_forever,daemon=True);thread.start()
    config={'base_url':f'http://127.0.0.1:{api.server_port}/LearnMore','read_token':READ}
    if can_write:config['write_token']=WRITE
    path=tmp_path/'config.json';path.write_text(json.dumps(config));path.chmod(0o600)
    async def exercise():
        env=dict(os.environ,LEARNMORE_MUSIC_CONFIG=str(path))
        params=StdioServerParameters(command=sys.executable,args=['-m','learnmore_music_mcp.server'],env=env)
        async with stdio_client(params) as (reader,writer):
            async with ClientSession(reader,writer) as session:
                await session.initialize()
                listing=await session.list_tools()
                names={t.name for t in listing.tools}
                assert len(names)==(5 if can_write else 4)
                assert ('apply_verified_alignment' in names)==can_write
                result=await session.call_tool('start_music_alignment',{'request_id':'same-request','youtube_url':'https://youtu.be/LjuJgrdh5eY'})
                assert not result.isError
                for name in ['get_music_job','get_alignment_report','preview_alignment_changes']:
                    assert not (await session.call_tool(name,{'job_id':jid})).isError
                invalid=await session.call_tool('get_music_job',{'job_id':'../apply'})
                assert invalid.isError
                if can_write:
                    assert not (await session.call_tool('apply_verified_alignment',{'job_id':jid,'preview_hash':'A'*64})).isError
    try:asyncio.run(exercise())
    finally:api.shutdown();api.server_close();thread.join()
    assert calls[0][3]['requestId']=='same-request'
    assert calls[0][1]=='/LearnMore/api/music-automation/jobs'
    assert all(c[2]=='Bearer '+READ for c in calls[:4])
    if can_write:
        assert calls[-1][2]=='Bearer '+WRITE
        assert calls[-1][3]['previewHash']=='A'*64
