IF DB_ID(N'TripDB') IS NULL CREATE DATABASE TripDB;
GO
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'trip_app')
    CREATE LOGIN trip_app WITH PASSWORD = '$(TripPassword)', CHECK_POLICY = ON;
GO
USE TripDB;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'trip_app')
    CREATE USER trip_app FOR LOGIN trip_app;
ALTER ROLE db_owner ADD MEMBER trip_app;
GO
