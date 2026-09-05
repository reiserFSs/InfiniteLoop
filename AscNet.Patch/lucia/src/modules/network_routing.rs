fn owned_path(path: &str) -> bool {
    let path = path.split(['?', '#']).next().unwrap_or(path);
    let notice = path
        .strip_prefix("client/notice/config/")
        .and_then(|path| path.rsplit('/').next());
    path.starts_with("client/config/")
        || path.starts_with("client/notice/html/")
        || matches!(
            notice,
            Some(
                "LoginNotice.json"
                    | "GameNotice.json"
                    | "SecondMenuNotice.json"
                    | "PopUpPicNotice.json"
            )
        )
}

pub(crate) fn redirected_url(origin: &str, url: &str) -> Option<String> {
    if !url.contains("://") {
        let suffix = url.trim_start_matches('/');
        return owned_path(suffix).then(|| format!("{origin}/prod/{suffix}"));
    }

    let scheme = url.find("://")?;
    let path = url[scheme + 3..]
        .find('/')
        .map(|offset| scheme + 3 + offset)?;
    let suffix = &url[path..];
    owned_path(suffix.strip_prefix("/prod/")?)
        .then(|| format!("{origin}{suffix}"))
}

#[cfg(test)]
mod tests {
    use super::redirected_url;

    const ORIGIN: &str = "http://127.0.0.1:8080";

    #[test]
    fn routes_only_server_owned_client_files() {
        for path in [
            "client/config/bootstrap.json",
            "client/notice/config/cdn-key/package/version/LoginNotice.json",
            "client/notice/config/cdn-key/package/version/GameNotice.json",
            "client/notice/config/cdn-key/package/version/SecondMenuNotice.json",
            "client/notice/config/cdn-key/package/version/PopUpPicNotice.json",
            "client/notice/html/cdn-key/package/version/en-US.html",
        ] {
            assert_eq!(
                redirected_url(ORIGIN, path),
                Some(format!("{ORIGIN}/prod/{path}"))
            );
            let absolute = format!("https://cdn.example/prod/{path}?v=1#section");
            assert_eq!(
                redirected_url(ORIGIN, &absolute),
                Some(format!("{ORIGIN}/prod/{path}?v=1#section"))
            );
        }

        assert_eq!(
            redirected_url(
                ORIGIN,
                "/client/notice/config/key/package/version/LoginNotice.json?lang=en"
            ),
            Some(format!(
                "{ORIGIN}/prod/client/notice/config/key/package/version/LoginNotice.json?lang=en"
            ))
        );

        for path in [
            "client/notice/config/cdn-key/package/version/ScrollPicNotice.json",
            "client/notice/config/cdn-key/package/version/ScrollTextNotice.json",
            "client/notice/pic/cdn-key/package/version/banner.png",
        ] {
            assert_eq!(redirected_url(ORIGIN, path), None);
            assert_eq!(
                redirected_url(ORIGIN, &format!("https://cdn.example/prod/{path}?v=1")),
                None
            );
        }
    }
}
