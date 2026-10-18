draughtsman 1
id doc_order_service
type architecture
title "Order service"
meta created=2026-09-20T22:00:00.000Z updated=2026-09-20T22:05:00.000Z by=eric
provenance claude-code updated 2026-09-20T22:05:00.000Z model=claude-fable-5-1 { n_cache e_3 }

node n_web service "Web app" @ 40,40 320x80
container c_vpc "VPC" @ 16,168 568x300 style.dash=dashed { n_api n_db n_cache }
node n_api service "Orders API" @ 40,200 320x80 ports=[in@0.5,0 db@0.25,1 cache@1,0.5] owner="platform"
node n_db database "Postgres" @ 40,360 160x80
node n_cache queue "Redis" @ 400,360 160x80

edge e_1 flow n_web -> n_api.in "HTTPS" routing=orthogonal
edge e_2 data n_api.db -> n_db "read/write" routing=orthogonal style.arrowStart=arrow style.arrowEnd=arrow
edge e_3 data n_api.cache -> n_cache routing=orthogonal waypoints=[480,240]
