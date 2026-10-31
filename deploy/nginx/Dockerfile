FROM nginx:stable-alpine
COPY build/web-rider/ /usr/share/nginx/html/
COPY build/web-driver/ /usr/share/nginx/html/driver/
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
