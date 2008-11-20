﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MySql.Data.MySqlClient;
using BKP.Online.Data;
using System.Security.Cryptography;
using System.IO;
using System.Data.Common;
using BKP.Online.Media;
using MusicImporter_Lib.Properties;
using BKP.Online.IO;

namespace MusicImporter_Lib
{
    /// <summary>
    /// 
    /// </summary>
    class ArtImporter
    {
        private IDatabase db = null;
        private string art_path = null;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="db"></param>
        /// <param name="art_path"></param>
        public ArtImporter(IDatabase db, string art_path)
        {
            this.db = db;
            this.art_path = art_path;
        }
        
        /// <summary>
        ///  insert album art
        /// </summary>
        /// <param name="tag">the id3 tag</param>
        /// <param name="current_dir">current directory</param>
        /// <returns>primary key (insert id)</returns>
        public string[] InsertArt(object song_id, TagLib.File tag_file)
        {
            string art = null;
            string key = null;
            byte[] hash = null;
            byte[] data = null;
            string type = string.Empty;
            string mime_type = string.Empty;
            string description = string.Empty;
            string current_dir = Path.GetDirectoryName(tag_file.Name);
            TagLib.Tag tag = tag_file.Tag;
            List<string> ids = new List<string>();
 
            foreach (TagLib.IPicture pic in tag.Pictures)
            {
              
                art = GenerateFileName( pic );
                data = new byte[pic.Data.Count];
                pic.Data.CopyTo(data, 0);
                type = pic.Type.ToString();
                mime_type = pic.MimeType;
                description = pic.Description;
                if (pic.MimeType != "-->") // no support for linked art
                {
                    string art_id = Insert(data, art, type, description, mime_type);
                    ids.Add(art_id);
                    CreateLink(song_id, art_id);
                }
            }


            // look for art in directory
            //string[] files = DirectoryExt.GetFiles(current_dir, Settings.Default.art_mask);
            //foreach (string file in files)
            //{
            //    string ext = Path.GetExtension(files[0]);
            //    art = guid.ToString("B") + ext;
            //    data = File.ReadAllBytes(files[0]);
            //    type = "Cover";
            //    mime_type = ext;
            //    description = Path.GetFileNameWithoutExtension( file );
            //}

            //return FindDefaultPicture(tag);

            return ids.ToArray(); // do wee need a ret val here?
        }

        /// <summary>
        /// generate file name for picture
        /// </summary>
        /// <param name="pic"></param>
        /// <returns></returns>
        private string GenerateFileName(TagLib.IPicture pic)
        {
            Guid guid = Guid.NewGuid();
            string mime_type = pic.MimeType.ToLower();
            return guid.ToString("B") + mime_type.Replace("image/", ".");
        }

        /// <summary>
        /// create link between song and art
        /// </summary>
        /// <param name="song_id"></param>
        /// <param name="art_id"></param>
        public void CreateLink(object song_id, object art_id)
        {
            string sql = "SELECT song_id FROM song_art WHERE song_id=?song_id AND art_id=?art_id";
            
            if ( db.Exists(sql) )
                return; // already in db

            sql = "INSERT INTO song_art VALUES(NULL, ?song_id, ?art_id, NULL, NOW())";
            MySqlCommand cmd = new MySqlCommand(sql);
            cmd.Parameters.AddWithValue("?song_id", song_id);
            cmd.Parameters.AddWithValue("?art_id", art_id);
            db.ExecuteNonQuery(cmd);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public string Insert(byte[] data, string art, string type, string description, string mime_type)
        {
            byte[] hash = ComputeHash(data);
            uint id = 0;
            string key = string.Empty;
            if (isDuplicateInsert(hash, out id))
            {
                string file = null;
                if (isOrphanedInsert(id, out file))
                {
                    // write file
                    SaveArt(file, data);
                }
                key = id.ToString();
            }
            else
            {
                // write file
                SaveArt(art, data);

                string sql = "INSERT INTO art VALUES(NULL, ?file, ?type, ?hash, ?description, ?mime_type, NULL, NOW())";
                MySqlCommand cmd = new MySqlCommand(sql);
                cmd.Parameters.AddWithValue("?file", art);
                cmd.Parameters.AddWithValue("?type", type);
                cmd.Parameters.AddWithValue("?hash", hash);
                cmd.Parameters.AddWithValue("?description", description);
                cmd.Parameters.AddWithValue("?mime_type", mime_type);
                db.ExecuteNonQuery(cmd);
                key = db.ExecuteScalar("SELECT LAST_INSERT_ID()").ToString();
            }
            return key;
        }
        
        /// <summary>
        /// get the first front cover or the first picture
        /// </summary>
        /// <param name="tag"></param>
        /// <returns></returns>
        public TagLib.IPicture FindDefaultPicture(TagLib.Tag tag)
        {
            if (tag.Pictures.Length < 1)
                return null;

            TagLib.IPicture pic = tag.Pictures[0];
            // find the first front cover image, if not use first image
            foreach (TagLib.IPicture p in tag.Pictures)
            {
                if (p.Type == TagLib.PictureType.FrontCover && p.MimeType != "-->")
                {
                    pic = p;
                    break;
                }
            }

            return pic;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="file"></param>
        /// <param name="data"></param>
        public void SaveArt(string file, byte[] data)
        {
            // write file to art location
            System.IO.File.WriteAllBytes(art_path + file, data);
            // gen & write thumbs
            GenerateThumbs(file);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="file_name"></param>
        private void GenerateThumbs(string file_name)
        {
            string art = art_path + file_name;
            Thumb.Generate(
                art_path + "large\\" + file_name, art, Settings.Default.art_large, 0, true);
            Thumb.Generate(
                art_path + "small\\" + file_name, art, Settings.Default.art_small, 0, true);
            Thumb.Generate(
                art_path + "xsmall\\" + file_name, art, Settings.Default.art_xsmall, 0, true);

        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="data"></param>
        /// <param name="obj"></param>
        /// <returns></returns>
        private bool isDuplicateInsert(byte[] hash, out uint id)
        {
            string sql = "SELECT id FROM art WHERE hash=?hash";
            MySqlCommand cmd = new MySqlCommand(sql);
            cmd.Parameters.AddWithValue("?hash", hash);
            object obj = null;
            try
            {
                obj = db.ExecuteScalar(cmd);
            }
            catch
            {
                obj = null;
            }

            id = 0;
            if (obj != null)
            {
                id = (uint)obj;
                return true;
            }
            
            return false;
        }
        /// <summary>
        /// returns wheater a given art id has matching file on disk
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        private bool isOrphanedInsert(long id, out string file)
        {
            string sql = "SELECT file FROM art WHERE id=?id";
            MySqlCommand cmd = new MySqlCommand(sql);
            cmd.Parameters.AddWithValue("?id", id);
            using (DbDataReader reader = db.ExecuteReader(cmd))
            {

                file = null;
                while (reader.Read())
                {
                    file = reader.GetString(0);
                }
            }
            return file != null && File.Exists( file );
        }
        /// <summary>
        /// compute a hash value
        /// </summary>
        /// <param name="data">data to hash</param>
        /// <returns>the hash value</returns>
        private byte[] ComputeHash(byte[] data)
        {
            MD5 md5 = new MD5CryptoServiceProvider();
            byte[] result = md5.ComputeHash(data);
            return result;
        }

        /// <summary>
        /// (Re)Scan and insert art from art directory
        /// </summary>
        //private void RescanArt()
        //{
        //    byte[] hash = null;
        //    string[] files = DirectoryExt.GetFiles( Settings.Default.art_location, "*.jpg;*.jpeg;*.png;*.bmp;*.gif" );
        //    for(int i = 0; i < files.Length; ++i)
        //    {
        //        OnMessage( "Processing Art: " + files[i] );
        //        string filename = Path.GetFileName( files[i] );
        //        string ext = Path.GetExtension( files[i] );
        //        byte[] data = null;
        //        string type = string.Empty;
        //        string mime_type = string.Empty;
        //        string description = string.Empty;
        //        data = File.ReadAllBytes( files[i] );
        //        type = "Cover";
        //        mime_type = ext;
        //        description = "cover art";
        //        hash = ComputeHash( data );
        //        string sql = "SELECT id FROM art WHERE hash=?hash";
        //        MySqlCommand cmd = new MySqlCommand( sql );
        //        cmd.Parameters.AddWithValue( "?hash", hash );
        //        object obj = null;
        //        obj = mysql_connection.ExecuteScalar( cmd );
        //        try
        //        {
        //            obj = mysql_connection.ExecuteScalar( cmd );
        //        }
        //        catch
        //        {
        //            //return;
        //        }
        //        if(obj == null)
        //        {
        //            sql = "INSERT INTO art VALUES(NULL, ?file, ?type, ?hash, ?description, ?mime_type, NULL, NOW())";
        //            cmd = new MySqlCommand( sql );
        //            cmd.Parameters.AddWithValue( "?file", filename );
        //            cmd.Parameters.AddWithValue( "?type", type );
        //            cmd.Parameters.AddWithValue( "?hash", hash );
        //            cmd.Parameters.AddWithValue( "?description", description );
        //            cmd.Parameters.AddWithValue( "?mime_type", mime_type );
        //            mysql_connection.ExecuteNonQuery( cmd );
        //        }
        //    }
        //}
    }
}
