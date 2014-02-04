using System;
using System.Data.Odbc;
using System.Security;
using System.Security.Cryptography;
using System.Net;
using System.Collections;
using System.Xml;
using System.Text;
using Server;
using Server.Misc;
using Server.Network;
using Server.Commands;

namespace Server.Accounting
{
	public class WebAccounting
	{

		public enum Status
		{
			Void = 0,
			Pending = 1,
			Active = 2,
			PWChanged = 3,
			EmailChanged = 4,
			Delete = 5
		}

		private static int QueryCount = 0; //Offset 0 values.

		public static bool UpdateOnWorldSave = true;
		public static bool UpdateOnWorldLoad = true;

		private static string
			DatabaseDriver = WAConfig.DatabaseDriver,
			DatabaseServer = WAConfig.DatabaseServer,
			DatabaseName = WAConfig.DatabaseName,
			DatabaseTable = WAConfig.DatabaseTable,
			DatabaseUserID = WAConfig.DatabaseUserID,
			DatabasePassword = WAConfig.DatabasePassword;

		static string ConnectionString = string.Format( "DRIVER={0};SERVER={1};DATABASE={2};UID={3};PASSWORD={4};CHARSET=UTF8",
			DatabaseDriver, DatabaseServer, DatabaseName, DatabaseUserID, DatabasePassword );

		static bool Synchronizing = false;

		public static void Initialize(){
			SynchronizeDatabase( );
			CommandSystem.Register( "AccSync", AccessLevel.Administrator, new CommandEventHandler( Sync_OnCommand ) );

			if( UpdateOnWorldLoad ){
				EventSink.WorldLoad += new WorldLoadEventHandler( OnLoaded );
			}

			if( UpdateOnWorldSave ){
				EventSink.WorldSave += new WorldSaveEventHandler( OnSaved );
			}else{
				Timer.DelayCall( TimeSpan.FromSeconds( 30.0 ), new TimerCallback( SynchronizeDatabase ) );
			}
		}

		public static void OnSaved( WorldSaveEventArgs e ){
			if( Synchronizing )
				return;

			SynchronizeDatabase( );
		}

		public static void OnLoaded( ){
			if( Synchronizing )
				return;

			SynchronizeDatabase( );
		}

		[Usage( "AccSync" )]
		[Description( "Synchronizes the Accounts Database" )]
		public static void Sync_OnCommand( CommandEventArgs e ){
			if( Synchronizing )
				return;

			Mobile from = e.Mobile;

			SynchronizeDatabase( );
			from.SendMessage( "Done Synchronizing Database!" );
		}


		//Gets all users from the db
		//if they dont exist in the UO XML
		//create them, passing the hashed password to the 
		public static void CreateAccountsFromDB( ){
			Console.WriteLine( "[CreateAccountsFromDB]" );
			try{
				ArrayList ToCreateFromDB = new ArrayList();
				OdbcConnection Connection = new OdbcConnection( ConnectionString );

				Connection.Open();
				OdbcCommand Command = Connection.CreateCommand( );

				Console.WriteLine("    Getting New Accounts From DB");

				Command.CommandText = string.Format( "SELECT username, password FROM {0} WHERE state='{1}'", DatabaseTable, ( int )Status.Pending );
				OdbcDataReader reader = Command.ExecuteReader( );
				
				QueryCount += 1;

				while( reader.Read() ){
					string username = reader.GetString(0);
					string password = reader.GetString(1);

					Console.WriteLine("Password from MySQL: {0}", password);

					//If user doesn't exist in xml
					if( Accounts.GetAccount(username) == null )
						//create a new user for xml and push to the array
						ToCreateFromDB.Add( Accounts.AddAccount(username, password) );
				}
				reader.Close();

				//Get the Accounts access level from the array
				foreach( Account a in ToCreateFromDB ){
					int ALevel = 0;

					if( a.AccessLevel == AccessLevel.Player ){
						ALevel = 1;
					}else if( a.AccessLevel == AccessLevel.Counselor ){
						ALevel = 2;
					}else if( a.AccessLevel == AccessLevel.GameMaster ){
						ALevel = 3;
					}else if( a.AccessLevel == AccessLevel.Seer ){
						ALevel = 4;
					}else if( a.AccessLevel == AccessLevel.Administrator ){
						ALevel = 6;
					}

					QueryCount += 1;
					
					Console.WriteLine( "    Updating Database With Accurate XML Information..." );
					Command.CommandText = string.Format( "UPDATE {0} SET state='{1}', access='{2}' WHERE username='{3}'", DatabaseTable, ( int )Status.Active, ALevel, a.Username );
					Command.ExecuteNonQuery();
				}

				Connection.Close();

				Console.WriteLine( "[{0} In-Game Accounts Created] ", ToCreateFromDB.Count );
			}
			catch( Exception e )
			{
				Console.WriteLine( "[In-Game Account Create] Error..." );
				Console.WriteLine( e );
			}
		}

		//Scans UO accounts.xml for all accounts
		//compares the usernames against the MySQL Query
		//any mismatches it adds them to the db
		public static void CreateAccountsFromUO(){
			Console.WriteLine( "[CreateAccountsFromUO]" );
			try{	
				ArrayList ToCreateFromUO = new ArrayList( );
				/**
				 * Query the database for all the user accounts
				 */
				OdbcConnection Connection = new OdbcConnection( ConnectionString );
				Connection.Open( );
				OdbcCommand Command = Connection.CreateCommand( );
				Console.WriteLine("    Exporting New Accounts from UO");
				Command.CommandText = string.Format( "SELECT username FROM {0}", DatabaseTable );
				OdbcDataReader reader = Command.ExecuteReader( );
				//Set the query count to start at 1
				QueryCount += 1;

				/**
				 * Iterate through the returned rows from the database
				 * Try to get a username from accounts.xml
				 * If we cant, add it to the array to be created later
				 */
				while( reader.Read() ){
					//set the username = to 0 of the array from db
					string username = reader.GetString( 0 );
					//Debugging purposes
					// Console.Write("User Returned from DB: ");
					// Console.WriteLine( username);

					//Try to find the user, returns username or null
					Account toCheck = Accounts.GetAccount( username ) as Account;

					// Console.Write("toCheck: ");
					// Console.WriteLine(toCheck);

					if( toCheck == null )
						//Account doesn't exist in accounts.xml, add it to the array
						ToCreateFromUO.Add( toCheck );

				}
				reader.Close();

				//iterate through all the accounts in the array
				foreach( Account a in ToCreateFromUO ){
					//Determine the access level of the account from xml
					int ALevel = 0;
					if( a.AccessLevel == AccessLevel.Player ){
						ALevel = 1;
					}else if( a.AccessLevel == AccessLevel.Counselor ){
						ALevel = 2;
					}else if( a.AccessLevel == AccessLevel.GameMaster )
					{
						ALevel = 3;
					}else if( a.AccessLevel == AccessLevel.Seer ){
						ALevel = 4;
					}else if( a.AccessLevel == AccessLevel.Administrator ){
						ALevel = 6;
					}

					//Get the password protection alg.. best to use sha1
					PasswordProtection PWMode = AccountHandler.ProtectPasswords;

					//make sure Password is empty
					string Password = "";

					switch( PWMode ){
						case PasswordProtection.None: { Password = a.PlainPassword; } break;
						case PasswordProtection.Crypt: { Password = a.CryptPassword; } break;
						default: { Password = a.NewCryptPassword; } break;
					}
					
					Console.WriteLine( "    Creating New User in Database..." );

					QueryCount += 1;

					OdbcCommand InsertCommand = Connection.CreateCommand( );

					InsertCommand.CommandText = string.Format( "INSERT INTO {0} (username,password,access,timestamp,state) VALUES( '{1}', '{2}', '{3}', '{4}', '{5}')", DatabaseTable, a.Username, Password, ALevel, ToUnixTimestamp( a.Created ), ( int )Status.Active );
					InsertCommand.ExecuteNonQuery( );
				}

				Connection.Close( );

				Console.WriteLine( "[{0} Database Accounts Added] ", ToCreateFromUO.Count );
			}
			catch( Exception e )
			{
				Console.WriteLine( "    Database Account Create Error..." );
				Console.WriteLine( e );
			}
		}

		public static void UpdateUOPasswords(){
			Console.WriteLine( "[UpdateUOPasswords]" );
			try{
				ArrayList ToUpdatePWFromDB = new ArrayList( );
				OdbcConnection Connection = new OdbcConnection( ConnectionString );

				Connection.Open( );
				OdbcCommand Command = Connection.CreateCommand( );
				Console.WriteLine("     Getting new password hashes from db");
				Command.CommandText = string.Format( "SELECT username, password FROM {0} WHERE state='{1}'", DatabaseTable, ( int )Status.PWChanged );
				OdbcDataReader reader = Command.ExecuteReader( );

				QueryCount += 1;
				//iterate through db accts which are status = 3
				while( reader.Read( ) ){
					string username = reader.GetString( 0 );
					string password = reader.GetString( 1 );
					//get the uo account data
					Account AtoUpdate = Accounts.GetAccount( username ) as Account;
					//if an account is found
					if( AtoUpdate != null )
					{
						//get the protection mode.
						PasswordProtection PWMode = AccountHandler.ProtectPasswords;
						//make password empty
						string Password = "";

						switch( PWMode )
						{
							//plain password
							case PasswordProtection.None: { Password = AtoUpdate.PlainPassword; } break;
							//crypto password (md5)
							case PasswordProtection.Crypt: { Password = AtoUpdate.CryptPassword; } break;
							//sha512 password
							default: { Password = AtoUpdate.NewCryptPassword; } break;
						}
						//if the password is empty, null or not = db password
						if( Password == null || Password == "" || Password != password )
						{
							//encrypt the password
							AtoUpdate.SetPassword( password );
							//push to the updatepwfromdb array
							ToUpdatePWFromDB.Add( AtoUpdate );
						}
					}
				}
				reader.Close( );

				//Console.WriteLine( "Updating Database..." );
				foreach( Account a in ToUpdatePWFromDB )
				{
					PasswordProtection PWModeU = AccountHandler.ProtectPasswords;
					string PasswordU = "";

					switch( PWModeU )
					{
						case PasswordProtection.None: { PasswordU = a.PlainPassword; } break;
						case PasswordProtection.Crypt: { PasswordU = a.CryptPassword; } break;
						default: { PasswordU = a.NewCryptPassword; } break;
					}

					QueryCount += 1;

					Command.CommandText = string.Format( "UPDATE {0} SET state='{1}',password='{2}' WHERE username='{3}'", DatabaseTable, ( int )Status.Active, PasswordU, a.Username );
					Command.ExecuteNonQuery( );
				}

				Connection.Close( );

				Console.WriteLine( "[{0} In-game Passwords Changed] ", ToUpdatePWFromDB.Count );
			}
			catch( System.Exception e )
			{
				Console.WriteLine( "[In-Game Password Change] Error..." );
				Console.WriteLine( e );
			}
		}

		public static void UpdateDBPasswords(){
			Console.WriteLine( "[UpdateDBPasswords] Exporting New Passwords..." );
			try{
				ArrayList ToUpdatePWFromUO = new ArrayList( );
				OdbcConnection Connection = new OdbcConnection( ConnectionString );

				Connection.Open( );
				OdbcCommand Command = Connection.CreateCommand( );

				Command.CommandText = string.Format( "SELECT username, password FROM {0} WHERE state='{1}'", DatabaseTable, ( int )Status.Active );
				OdbcDataReader reader = Command.ExecuteReader( );

				QueryCount += 1;

				while( reader.Read( ) ){
					string username = reader.GetString( 0 );
					string password = reader.GetString( 1 );

					Account AtoUpdate = Accounts.GetAccount( username ) as Account;

					if( AtoUpdate != null ){
						PasswordProtection PWMode = AccountHandler.ProtectPasswords;
						string Password = "";

						switch( PWMode ){
							case PasswordProtection.None: { Password = AtoUpdate.PlainPassword; } break;
							case PasswordProtection.Crypt: { Password = AtoUpdate.CryptPassword; } break;
							default: { Password = AtoUpdate.NewCryptPassword; } break;
						}

						if( Password == null || Password == "" || Password != password ){
							ToUpdatePWFromUO.Add( AtoUpdate );
						}
					}
				}
				reader.Close( );

				Console.WriteLine( "[UpdateDBPasswords] Updating Database..." );
				foreach( Account a in ToUpdatePWFromUO ){
					PasswordProtection PWModeU = AccountHandler.ProtectPasswords;
					string PasswordU = "";

					switch( PWModeU )
					{
						case PasswordProtection.None: { PasswordU = a.PlainPassword; } break;
						case PasswordProtection.Crypt: { PasswordU = a.CryptPassword; } break;
						default: { PasswordU = a.NewCryptPassword; } break;
					}

					QueryCount += 1;

					Command.CommandText = string.Format( "UPDATE {0} SET state='{1}', password='{2}' WHERE username='{3}'", DatabaseTable, ( int )Status.Active, PasswordU, a.Username );
					Command.ExecuteNonQuery( );
				}

				Connection.Close( );

				Console.WriteLine( "[{0} Database Passwords Changed] ", ToUpdatePWFromUO.Count );
			}
			catch( Exception e )
			{
				Console.WriteLine( "[Database Password Change] Error..." );
				Console.WriteLine( e );
			}
		}

		public static void SynchronizeDatabase(){
			if( Synchronizing || !WAConfig.Enabled )
				return;

			Synchronizing = true;

			Console.WriteLine( "[Starting Accounting System]" );

			// get accounts from DB and create UO XML accounts
			CreateAccountsFromDB();
			// Password on DB has changed, update the UO XML for the account
			//UpdateUOPasswords();
			
			//get accounts from UO XML and create DB accounts
			//CreateAccountsFromUO( );

			
			// UpdateDBPasswords( );

			Console.WriteLine( string.Format( "[Executed {0} Database Queries]", QueryCount.ToString( ) ) );
			QueryCount = 0;
			World.Save();
			Synchronizing = false;
		}

		static double ToUnixTimestamp( DateTime date ){
			DateTime origin = new DateTime( 1970, 1, 1, 0, 0, 0, 0 );
			TimeSpan diff = date - origin;
			return Math.Floor( diff.TotalSeconds );
		}
	}
}