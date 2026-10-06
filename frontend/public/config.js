// Runtime settings for the FHSMS / AgriLink web app.
// You can edit this file directly on your server - no rebuild needed.
//
// apiBaseUrl:
//   ""  (empty)  -> use "/api" on the same domain as this site. This is right
//                   for the Docker setup, and for any nginx/Apache/IIS setup
//                   that forwards /api to the backend.
//   "https://api.example.com/api"  -> use this if the API is on a different
//                   domain (e.g. website on cPanel shared hosting, API on a VPS).
//                   Remember to add this site's URL to ALLOWED_ORIGINS on the API.
window.__FHSMS_CONFIG__ = {
  apiBaseUrl: ""
};
