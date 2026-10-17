-- Local development credentials only. Azure credentials will be injected at deployment time.
CREATE ROLE identity_app LOGIN PASSWORD 'identity_local_only';
CREATE ROLE catalog_app LOGIN PASSWORD 'catalog_local_only';
CREATE ROLE order_app LOGIN PASSWORD 'order_local_only';
CREATE ROLE fulfilment_app LOGIN PASSWORD 'fulfilment_local_only';

CREATE DATABASE identity_db OWNER identity_app;
CREATE DATABASE catalog_db OWNER catalog_app;
CREATE DATABASE order_db OWNER order_app;
CREATE DATABASE fulfilment_db OWNER fulfilment_app;

REVOKE ALL ON DATABASE identity_db FROM PUBLIC;
REVOKE ALL ON DATABASE catalog_db FROM PUBLIC;
REVOKE ALL ON DATABASE order_db FROM PUBLIC;
REVOKE ALL ON DATABASE fulfilment_db FROM PUBLIC;

GRANT CONNECT ON DATABASE identity_db TO identity_app;
GRANT CONNECT ON DATABASE catalog_db TO catalog_app;
GRANT CONNECT ON DATABASE order_db TO order_app;
GRANT CONNECT ON DATABASE fulfilment_db TO fulfilment_app;
